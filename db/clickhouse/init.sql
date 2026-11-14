-- Схема OLAP ClickHouse для отчётности BionicPRO.
-- Каждый DDL идемпотентен (IF NOT EXISTS): повторы задач и бэкфиллы безопасны.

CREATE DATABASE IF NOT EXISTS bionicpro_olap;

-- Реплицированный CRM-справочник: атрибуты клиента плюс метаданные протеза,
-- одна строка на (username, prosthesis_id).
CREATE TABLE IF NOT EXISTS bionicpro_olap.crm_customers_prostheses
(
    username          String,
    customer_id       Int32,
    email             String,
    full_name         String,
    country_code      LowCardinality(String),
    prosthesis_id     String,
    model_name        LowCardinality(String),
    chip_version      LowCardinality(String),
    manufactured_date Date,
    loaded_at         DateTime64(3)
)
ENGINE = ReplacingMergeTree(loaded_at)
ORDER BY (username, prosthesis_id);

-- Сырая телеметрия, скопированная из PostgreSQL-источника.
CREATE TABLE IF NOT EXISTS bionicpro_olap.raw_sensor_telemetry
(
    event_id          String,
    prosthesis_id     String,
    event_date        Date MATERIALIZED toDate(recorded_at),
    recorded_at       DateTime64(3),
    myo_signal_mv     Float32,
    noise_ratio       Float32,
    movement_type     LowCardinality(String),
    actuator_success  Bool,
    response_time_ms  UInt16,
    battery_pct       UInt8,
    battery_temp_c    Float32,
    loaded_at         DateTime64(3)
)
ENGINE = ReplacingMergeTree(loaded_at)
PARTITION BY toYYYYMM(event_date)
ORDER BY (prosthesis_id, event_date, event_id);

-- Предагрегированная суточная витрина. Ключ сортировки
-- (username, report_date, prosthesis_id) позволяет первичному индексу
-- сразу находить окно дат одного пользователя -- без соединений
-- и сканирования сырых событий.
CREATE TABLE IF NOT EXISTS bionicpro_olap.prosthesis_reports_mart
(
    username                  String,
    customer_id               Int32,
    email                     String,
    full_name                 String,
    prosthesis_id             String,
    model_name                LowCardinality(String),
    chip_version              LowCardinality(String),
    report_date               Date,
    total_events              UInt64,
    successful_movements      UInt64,
    failed_movements          UInt64,
    avg_response_time_ms      Float64,
    p95_response_time_ms      Float64,
    slow_responses_over_100ms UInt64,
    avg_myo_signal_mv         Float64,
    avg_noise_ratio           Float64,
    min_battery_pct           UInt8,
    avg_battery_pct           Float64,
    max_battery_temp_c        Float32,
    updated_at                DateTime64(3)
)
ENGINE = ReplacingMergeTree(updated_at)
PARTITION BY toYYYYMM(report_date)
ORDER BY (username, report_date, prosthesis_id);

-- Отметки ETL: одна строка на полностью загруженный report_date.
-- Строка на каждый день выявляет пропуски в любом диапазоне [from, to]
-- и не мешает параллельным запускам бэкфилла.
CREATE TABLE IF NOT EXISTS bionicpro_olap.etl_processed_dates
(
    pipeline_name LowCardinality(String),
    report_date   Date,
    processed_at  DateTime64(3)
)
ENGINE = ReplacingMergeTree(processed_at)
ORDER BY (pipeline_name, report_date);
