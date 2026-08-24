-- PersonCleaner schema 1 -> 2
-- Apply only while Emby is stopped.
BEGIN IMMEDIATE;
ALTER TABLE provider_media ADD COLUMN slug TEXT;
UPDATE schema_info SET version=2 WHERE singleton=1 AND version=1;
COMMIT;
