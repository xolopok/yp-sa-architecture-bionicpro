-- База телеметрии BionicPRO: сырые показания миодатчиков,
-- поступающие с чипов протезов через 4G-модуль.
--
-- Покрытие -- от якоря ETL (2026-10-30, тот же литерал, что START_DATE в DAG)
-- до текущего дня плюс 12 часов. Завершенные дни закрыты и готовы к суточному
-- батчу Airflow; текущий день остается в OLTP необработанным до закрытия
-- расчетного окна -- такие периоды сервис отчётов должен отклонять.

CREATE TABLE sensor_telemetry (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prosthesis_id TEXT NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL,
    myo_signal_mv REAL NOT NULL,
    noise_ratio REAL NOT NULL,
    movement_type TEXT NOT NULL,
    actuator_success BOOLEAN NOT NULL,
    response_time_ms INTEGER NOT NULL,
    battery_pct SMALLINT NOT NULL,
    battery_temp_c REAL NOT NULL
);

CREATE INDEX ix_sensor_telemetry_recorded_at_prosthesis
    ON sensor_telemetry (recorded_at, prosthesis_id);

INSERT INTO sensor_telemetry (
    prosthesis_id, recorded_at, myo_signal_mv, noise_ratio, movement_type,
    actuator_success, response_time_ms, battery_pct, battery_temp_c
)
SELECT
    p.prosthesis_id,
    ts,
    1.0 + (abs(hashtext(p.prosthesis_id || ts::text)) % 80) / 10.0,
    0.01 + (abs(hashtext(ts::text || p.prosthesis_id)) % 15) / 100.0,
    (ARRAY['grip', 'pinch', 'rotate', 'flex', 'extend'])
        [1 + abs(hashtext(p.prosthesis_id || ts::text)) % 5],
    abs(hashtext(ts::text)) % 10 <> 0,
    40 + abs(hashtext(p.prosthesis_id || ts::text)) % 110,
    30 + (abs(hashtext(ts::text || p.prosthesis_id)) % 66),
    28.0 + (abs(hashtext(ts::text)) % 90) / 10.0
FROM generate_series(
        TIMESTAMPTZ '2026-10-30 00:00:00+00',
        date_trunc('hour', now()) + interval '12 hours',
        interval '90 minutes'
     ) AS ts
CROSS JOIN (VALUES
    ('BP-RU-0001'),
    ('BP-RU-0002'),
    ('BP-RU-0003'),
    ('BP-RU-0004')
) AS p (prosthesis_id);
