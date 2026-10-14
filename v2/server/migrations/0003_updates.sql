-- Automatic updates of the Windows client.

-- Client versions published as GitHub Releases (tag client-v<version>), found by the hourly check.
CREATE TABLE releases (
  version       TEXT    PRIMARY KEY,
  tag           TEXT    NOT NULL,
  url           TEXT    NOT NULL,   -- download URL of MonitorV2-<version>.zip
  sha256        TEXT    NOT NULL,   -- lowercase hex, from MonitorV2-<version>.zip.sha256
  size          INTEGER NOT NULL,
  published_at  INTEGER NOT NULL,
  notes         TEXT,
  found_at      INTEGER NOT NULL
);

-- Small key/value store (e.g. when GitHub was last checked).
CREATE TABLE meta (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL
);

-- The version a parent asked this PC to install, and how that went.
--   update_status: requested | installing | done | failed
ALTER TABLE devices ADD COLUMN target_version TEXT;
ALTER TABLE devices ADD COLUMN update_requested_at INTEGER;
ALTER TABLE devices ADD COLUMN update_status TEXT;
ALTER TABLE devices ADD COLUMN update_message TEXT;
ALTER TABLE devices ADD COLUMN update_at INTEGER;
