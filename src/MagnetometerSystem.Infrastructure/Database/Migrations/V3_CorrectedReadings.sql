-- V3: 校正读数表（独立存储，不覆盖原始数据）
CREATE TABLE IF NOT EXISTS corrected_readings (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    original_reading_id     INTEGER NOT NULL,
    session_id              TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    timestamp               TEXT NOT NULL,
    correction_profile_id   TEXT NOT NULL,
    ch0                     REAL,
    ch1                     REAL,
    ch2                     REAL,
    ch3                     REAL,
    ch4                     REAL,
    ch5                     REAL,
    extra_channels          TEXT,
    corrected_total_field   REAL,
    is_ortho_corrected      INTEGER DEFAULT 1,
    corrected_at            TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_corrected_session ON corrected_readings(session_id);
CREATE INDEX IF NOT EXISTS idx_corrected_profile ON corrected_readings(session_id, correction_profile_id);
