-- V4: 添加GPS坐标字段
ALTER TABLE readings ADD COLUMN latitude REAL;
ALTER TABLE readings ADD COLUMN longitude REAL;
ALTER TABLE readings ADD COLUMN altitude REAL;

-- 会话表标记是否包含GPS数据
ALTER TABLE sessions ADD COLUMN has_gps_data INTEGER DEFAULT 0;
