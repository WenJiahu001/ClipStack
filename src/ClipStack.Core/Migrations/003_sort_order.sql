-- 排序序号与真实复制时间分离，避免同毫秒并列及系统时钟回拨影响置顶。
ALTER TABLE clips ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;
WITH ranks AS (SELECT id, ROW_NUMBER() OVER (ORDER BY copied_utc, id) AS rank FROM clips)
UPDATE clips SET sort_order=(SELECT rank FROM ranks WHERE ranks.id=clips.id);
CREATE INDEX ix_clips_sort ON clips(sort_order DESC);
PRAGMA user_version = 3;
