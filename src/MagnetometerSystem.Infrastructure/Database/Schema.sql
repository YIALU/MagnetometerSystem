-- 统一 schema：所有表与索引
-- 协议驱动：readings / corrected_readings 的通道数据以 JSON blob 形式存储

CREATE TABLE IF NOT EXISTS sessions (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    started_at      TEXT NOT NULL,
    ended_at        TEXT,
    sensor_type     TEXT NOT NULL,
    sample_rate     REAL NOT NULL,
    channel_count   INTEGER NOT NULL,
    channel_names   TEXT,
    device_info     TEXT,
    connection_type TEXT,
    notes           TEXT,
    total_readings  INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS readings (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id  TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    timestamp   TEXT NOT NULL,
    data        TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_readings_session_time
    ON readings(session_id, timestamp);

CREATE TABLE IF NOT EXISTS corrected_readings (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    original_reading_id     INTEGER NOT NULL,
    session_id              TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    timestamp               TEXT NOT NULL,
    correction_profile_id   TEXT NOT NULL,
    data                    TEXT NOT NULL,
    corrected_at            TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_corrected_session ON corrected_readings(session_id);
CREATE INDEX IF NOT EXISTS idx_corrected_profile ON corrected_readings(session_id, correction_profile_id);

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

CREATE TABLE IF NOT EXISTS orthogonality_calibrations (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id   TEXT NOT NULL,
    session_id  TEXT NOT NULL,
    matrix_json TEXT NOT NULL,
    created_at  TEXT NOT NULL,
    operator    TEXT,
    notes       TEXT
);

CREATE INDEX IF NOT EXISTS idx_ortho_device ON orthogonality_calibrations(device_id);
CREATE INDEX IF NOT EXISTS idx_ortho_session ON orthogonality_calibrations(session_id);

CREATE TABLE IF NOT EXISTS user_preferences (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS settings (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
