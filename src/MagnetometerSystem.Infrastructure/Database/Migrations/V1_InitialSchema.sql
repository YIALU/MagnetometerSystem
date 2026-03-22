CREATE TABLE IF NOT EXISTS sessions (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    started_at      TEXT NOT NULL,
    ended_at        TEXT,
    sensor_type     TEXT NOT NULL,
    sample_rate     REAL NOT NULL,
    channel_count   INTEGER NOT NULL,
    channel_names   TEXT,           -- JSON 数组: ["X","Y","Z"]
    device_info     TEXT,           -- 设备信息 (序列号等)
    connection_type TEXT,           -- "Serial" | "Tcp"
    notes           TEXT,
    total_readings  INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS readings (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id          TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    timestamp           TEXT NOT NULL,   -- ISO 8601 格式
    ch0                 REAL,
    ch1                 REAL,
    ch2                 REAL,
    ch3                 REAL,
    ch4                 REAL,
    ch5                 REAL,
    extra_channels      TEXT,            -- 当通道数 > 6 时, 存储为 JSON 数组
    total_field         REAL,
    is_calibrated       INTEGER DEFAULT 0,
    is_ortho_corrected  INTEGER DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_readings_session_time
    ON readings(session_id, timestamp);

CREATE TABLE IF NOT EXISTS settings (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
