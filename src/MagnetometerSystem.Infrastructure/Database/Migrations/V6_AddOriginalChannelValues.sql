-- V6: 添加原始通道值字段（用于保存校正前的数据）
ALTER TABLE readings ADD COLUMN original_channel_values TEXT;
