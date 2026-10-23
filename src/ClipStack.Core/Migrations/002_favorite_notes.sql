-- 应用会在迁移前备份旧库，并在同一个事务内执行本文件。
-- 备注使用 Windows 当前用户 DPAPI 加密后写入，空备注为 NULL。
ALTER TABLE clips ADD COLUMN note_payload BLOB;
PRAGMA user_version = 2;
