"""Ежедневный ETL-пайплайн отчётности BionicPRO.

Переносит справочники CRM и сырую телеметрию из PostgreSQL в ClickHouse,
строит предагрегированную суточную витрину и записывает отметки обработанных
дат -- по ним API отчётов проверяет запрошенный период.
"""
from __future__ import annotations

import os
from datetime import datetime, timedelta, timezone

import clickhouse_connect
import pendulum
import psycopg2
from airflow import DAG
from airflow.hooks.base import BaseHook
from airflow.operators.python import PythonOperator

# Якорь бэкфилла: телеметрия в db/telemetry/init.sql сеется с этой же даты,
# а catchup=True догоняет каждый завершенный день. После первого развертывания
# менять нельзя: сдвиг якоря ломает соответствие бэкфилла данным в OLTP.
START_DATE = pendulum.datetime(2026, 10, 30, tz="UTC")

PIPELINE_NAME = "bionicpro_reports_etl"

CRM_COLUMNS = [
    "username",
    "customer_id",
    "email",
    "full_name",
    "country_code",
    "prosthesis_id",
    "model_name",
    "chip_version",
    "manufactured_date",
    "loaded_at",
]

TELEMETRY_COLUMNS = [
    "event_id",
    "prosthesis_id",
    "recorded_at",
    "myo_signal_mv",
    "noise_ratio",
    "movement_type",
    "actuator_success",
    "response_time_ms",
    "battery_pct",
    "battery_temp_c",
    "loaded_at",
]


def _crm_connection_params() -> dict:
    """Собирает параметры подключения к CRM-базе."""
    conn = BaseHook.get_connection("crm_db")
    return {
        "host": conn.host,
        "port": conn.port or 5432,
        "dbname": conn.schema,
        "user": conn.login,
        "password": conn.password,
    }


def _telemetry_connection_params() -> dict:
    """Собирает параметры подключения к базе телеметрии."""
    conn = BaseHook.get_connection("telemetry_db")
    return {
        "host": conn.host,
        "port": conn.port or 5432,
        "dbname": conn.schema,
        "user": conn.login,
        "password": conn.password,
    }


def _clickhouse_client():
    """Создает HTTP-клиент ClickHouse для OLAP-базы."""
    return clickhouse_connect.get_client(
        host=os.environ["CLICKHOUSE_HOST"],
        port=int(os.environ.get("CLICKHOUSE_PORT", "8123")),
        username=os.environ.get("CLICKHOUSE_USER", "default"),
        password=os.environ.get("CLICKHOUSE_PASSWORD", ""),
        database="bionicpro_olap",
    )


def ensure_olap_schema(**_context) -> None:
    """Создает базу bionicpro_olap и все четыре таблицы при отсутствии."""
    with _clickhouse_client() as client:
        client.command("CREATE DATABASE IF NOT EXISTS bionicpro_olap")
        client.command(
            """
            CREATE TABLE IF NOT EXISTS bionicpro_olap.crm_customers_prostheses
            (
                username String,
                customer_id Int32,
                email String,
                full_name String,
                country_code LowCardinality(String),
                prosthesis_id String,
                model_name LowCardinality(String),
                chip_version LowCardinality(String),
                manufactured_date Date,
                loaded_at DateTime64(3)
            )
            ENGINE = ReplacingMergeTree(loaded_at)
            ORDER BY (username, prosthesis_id)
            """
        )
        client.command(
            """
            CREATE TABLE IF NOT EXISTS bionicpro_olap.raw_sensor_telemetry
            (
                event_id String,
                prosthesis_id String,
                event_date Date MATERIALIZED toDate(recorded_at),
                recorded_at DateTime64(3),
                myo_signal_mv Float32,
                noise_ratio Float32,
                movement_type LowCardinality(String),
                actuator_success Bool,
                response_time_ms UInt16,
                battery_pct UInt8,
                battery_temp_c Float32,
                loaded_at DateTime64(3)
            )
            ENGINE = ReplacingMergeTree(loaded_at)
            PARTITION BY toYYYYMM(event_date)
            ORDER BY (prosthesis_id, event_date, event_id)
            """
        )
        client.command(
            """
            CREATE TABLE IF NOT EXISTS bionicpro_olap.prosthesis_reports_mart
            (
                username String,
                customer_id Int32,
                email String,
                full_name String,
                prosthesis_id String,
                model_name LowCardinality(String),
                chip_version LowCardinality(String),
                report_date Date,
                total_events UInt64,
                successful_movements UInt64,
                failed_movements UInt64,
                avg_response_time_ms Float64,
                p95_response_time_ms Float64,
                slow_responses_over_100ms UInt64,
                avg_myo_signal_mv Float64,
                avg_noise_ratio Float64,
                min_battery_pct UInt8,
                avg_battery_pct Float64,
                max_battery_temp_c Float32,
                updated_at DateTime64(3)
            )
            ENGINE = ReplacingMergeTree(updated_at)
            PARTITION BY toYYYYMM(report_date)
            ORDER BY (username, report_date, prosthesis_id)
            """
        )
        client.command(
            """
            CREATE TABLE IF NOT EXISTS bionicpro_olap.etl_processed_dates
            (
                pipeline_name LowCardinality(String),
                report_date Date,
                processed_at DateTime64(3)
            )
            ENGINE = ReplacingMergeTree(processed_at)
            ORDER BY (pipeline_name, report_date)
            """
        )


def extract_crm_to_olap(**_context) -> int:
    """Реплицирует клиентов и протезы CRM в OLAP-справочник."""
    with psycopg2.connect(**_crm_connection_params()) as pg, pg.cursor() as cursor:
        cursor.execute(
            """
            SELECT c.username, c.customer_id, c.email, c.full_name, c.country_code,
                   p.prosthesis_id, p.model_name, p.chip_version, p.manufactured_date
            FROM customers c
            INNER JOIN prostheses p ON p.customer_id = c.customer_id
            """
        )
        loaded_at = datetime.now(timezone.utc)
        rows = [list(row) + [loaded_at] for row in cursor.fetchall()]

    with _clickhouse_client() as client:
        client.insert(
            "crm_customers_prostheses",
            rows,
            column_names=CRM_COLUMNS,
        )
    return len(rows)


def extract_telemetry_to_olap(**context) -> int:
    """Размещает сырую телеметрию обработанного UTC-дня в ClickHouse."""
    interval_start = context["data_interval_start"]
    interval_end = context["data_interval_end"]

    with psycopg2.connect(**_telemetry_connection_params()) as pg, pg.cursor() as cursor:
        cursor.execute(
            """
            SELECT event_id::text, prosthesis_id, recorded_at, myo_signal_mv,
                   noise_ratio, movement_type, actuator_success, response_time_ms,
                   battery_pct, battery_temp_c
            FROM sensor_telemetry
            WHERE recorded_at >= %s AND recorded_at < %s
            """,
            (interval_start, interval_end),
        )
        loaded_at = datetime.now(timezone.utc)
        rows = [list(row) + [loaded_at] for row in cursor.fetchall()]

    with _clickhouse_client() as client:
        client.insert(
            "raw_sensor_telemetry",
            rows,
            column_names=TELEMETRY_COLUMNS,
        )
    return len(rows)


def build_reports_mart(**context) -> str:
    """Агрегирует размещенный день в предагрегированную витрину отчётности."""
    ds = context["ds"]

    with _clickhouse_client() as client:
        client.command(
            """
            INSERT INTO bionicpro_olap.prosthesis_reports_mart
            SELECT
                c.username,
                c.customer_id,
                c.email,
                c.full_name,
                r.prosthesis_id,
                c.model_name,
                c.chip_version,
                toDate({ds:String}) AS report_date,
                count() AS total_events,
                countIf(r.actuator_success) AS successful_movements,
                countIf(NOT r.actuator_success) AS failed_movements,
                avg(r.response_time_ms) AS avg_response_time_ms,
                quantileExact(0.95)(r.response_time_ms) AS p95_response_time_ms,
                countIf(r.response_time_ms > 100) AS slow_responses_over_100ms,
                avg(r.myo_signal_mv) AS avg_myo_signal_mv,
                avg(r.noise_ratio) AS avg_noise_ratio,
                min(r.battery_pct) AS min_battery_pct,
                avg(r.battery_pct) AS avg_battery_pct,
                max(r.battery_temp_c) AS max_battery_temp_c,
                now64(3) AS updated_at
            FROM bionicpro_olap.raw_sensor_telemetry AS r FINAL
            INNER JOIN bionicpro_olap.crm_customers_prostheses AS c FINAL
                USING (prosthesis_id)
            WHERE r.event_date = toDate({ds:String})
            GROUP BY
                c.username, c.customer_id, c.email, c.full_name,
                r.prosthesis_id, c.model_name, c.chip_version
            """,
            parameters={"ds": ds},
        )
    return f"mart built for {ds}"


def record_processed_date(**context) -> str:
    """Отмечает обработанный день в таблице отметок ETL."""
    from datetime import date

    report_date = date.fromisoformat(context["ds"])

    with _clickhouse_client() as client:
        client.insert(
            "etl_processed_dates",
            [(PIPELINE_NAME, report_date, datetime.now(timezone.utc))],
            column_names=["pipeline_name", "report_date", "processed_at"],
        )
    return f"watermark recorded for {report_date}"


with DAG(
    dag_id=PIPELINE_NAME,
    description="Ежедневный ETL: CRM и телеметрия -> витрина отчётности в ClickHouse",
    schedule="@daily",
    start_date=START_DATE,
    catchup=True,
    max_active_runs=4,
    is_paused_upon_creation=False,
    default_args={"retries": 2, "retry_delay": timedelta(minutes=1)},
    tags=["bionicpro", "reports"],
) as dag:
    t_ensure_schema = PythonOperator(
        task_id="ensure_olap_schema",
        python_callable=ensure_olap_schema,
    )
    t_extract_crm = PythonOperator(
        task_id="extract_crm_to_olap",
        python_callable=extract_crm_to_olap,
    )
    t_extract_telemetry = PythonOperator(
        task_id="extract_telemetry_to_olap",
        python_callable=extract_telemetry_to_olap,
    )
    t_build_mart = PythonOperator(
        task_id="build_reports_mart",
        python_callable=build_reports_mart,
    )
    t_record_date = PythonOperator(
        task_id="record_processed_date",
        python_callable=record_processed_date,
    )

    t_ensure_schema >> [t_extract_crm, t_extract_telemetry]
    [t_extract_crm, t_extract_telemetry] >> t_build_mart >> t_record_date
