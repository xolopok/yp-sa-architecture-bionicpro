-- CRM-база-источник BionicPRO: клиенты и их протезы.
-- Имена пользователей соответствуют аккаунтам realm Keycloak из keycloak/realm-export.json.

CREATE TABLE customers (
    customer_id SERIAL PRIMARY KEY,
    username TEXT NOT NULL UNIQUE,
    email TEXT NOT NULL,
    full_name TEXT NOT NULL,
    country_code TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE prostheses (
    prosthesis_id TEXT PRIMARY KEY,
    customer_id INTEGER NOT NULL REFERENCES customers (customer_id),
    model_name TEXT NOT NULL,
    chip_version TEXT NOT NULL,
    manufactured_date DATE NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO customers (username, email, full_name, country_code) VALUES
    ('prothetic1', 'prothetic1@example.com', 'Ivan Petrov', 'RU'),
    ('prothetic2', 'prothetic2@example.com', 'Maria Sidorova', 'RU'),
    ('prothetic3', 'prothetic3@example.com', 'Alexey Smirnov', 'RU');

INSERT INTO prostheses (prosthesis_id, customer_id, model_name, chip_version, manufactured_date) VALUES
    ('BP-RU-0001', 1, 'BionicArm Pro', 'ESP32-v3.1', DATE '2026-01-15'),
    ('BP-RU-0002', 2, 'BionicArm Lite', 'ESP32-v2.4', DATE '2026-03-02'),
    ('BP-RU-0003', 3, 'BionicLeg Pro', 'ESP32-v3.1', DATE '2026-05-20'),
    ('BP-RU-0004', 1, 'BionicArm Pro', 'ESP32-v3.2', DATE '2026-08-11');
