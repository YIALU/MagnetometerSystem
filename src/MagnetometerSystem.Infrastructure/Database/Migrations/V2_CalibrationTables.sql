CREATE TABLE IF NOT EXISTS orthogonality_profiles (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    sensor_serial   TEXT,
    created_at      TEXT NOT NULL,
    offset_x        REAL NOT NULL,
    offset_y        REAL NOT NULL,
    offset_z        REAL NOT NULL,
    m00 REAL NOT NULL, m01 REAL NOT NULL, m02 REAL NOT NULL,
    m10 REAL NOT NULL, m11 REAL NOT NULL, m12 REAL NOT NULL,
    m20 REAL NOT NULL, m21 REAL NOT NULL, m22 REAL NOT NULL,
    residual_mean   REAL,
    residual_std    REAL,
    sample_count    INTEGER,
    notes           TEXT
);

CREATE TABLE IF NOT EXISTS calibration_profiles (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    sensor_type     TEXT NOT NULL,
    sensor_serial   TEXT,
    created_at      TEXT NOT NULL,
    offset_values   TEXT NOT NULL,
    gain_values     TEXT NOT NULL,
    notes           TEXT
);
