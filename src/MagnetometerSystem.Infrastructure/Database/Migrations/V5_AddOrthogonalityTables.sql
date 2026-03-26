-- V5: 添加正交度校准记录表
CREATE TABLE IF NOT EXISTS orthogonality_calibrations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    matrix_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    operator TEXT,
    notes TEXT
);

CREATE INDEX IF NOT EXISTS idx_ortho_device ON orthogonality_calibrations(device_id);
CREATE INDEX IF NOT EXISTS idx_ortho_session ON orthogonality_calibrations(session_id);
