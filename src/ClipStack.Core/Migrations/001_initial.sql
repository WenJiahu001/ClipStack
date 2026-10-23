-- 此迁移由应用在事务内执行，user_version 同事务提交。
CREATE TABLE clips (
    id TEXT PRIMARY KEY NOT NULL,
    kind INTEGER NOT NULL CHECK (kind IN (1, 2)),
    fingerprint TEXT NOT NULL UNIQUE,
    text_payload BLOB,
    image_file TEXT,
    thumbnail_file TEXT,
    size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
    width INTEGER NOT NULL DEFAULT 0,
    height INTEGER NOT NULL DEFAULT 0,
    created_utc INTEGER NOT NULL,
    copied_utc INTEGER NOT NULL,
    used_utc INTEGER,
    favorite INTEGER NOT NULL DEFAULT 0 CHECK (favorite IN (0, 1)),
    CHECK ((kind = 1 AND text_payload IS NOT NULL AND image_file IS NULL)
        OR (kind = 2 AND text_payload IS NULL AND image_file IS NOT NULL))
);
CREATE INDEX ix_clips_copied ON clips(copied_utc DESC);
PRAGMA application_id = 1129072976;
PRAGMA user_version = 1;
