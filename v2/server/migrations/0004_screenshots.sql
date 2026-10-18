-- Screenshots sent by the PCs, kept for 3 days (the cron deletes older ones) and shown in the web app.
CREATE TABLE screenshots (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  device_id     TEXT    NOT NULL,
  at            INTEGER NOT NULL,   -- server time it arrived
  day           TEXT    NOT NULL,   -- calendar day of `at` in the configured time zone
  mode          TEXT    NOT NULL CHECK (mode IN ('school', 'gaming')),
  content_type  TEXT    NOT NULL,
  size          INTEGER NOT NULL,
  image         BLOB    NOT NULL
);
CREATE INDEX screenshots_at ON screenshots (at);
CREATE INDEX screenshots_day ON screenshots (day, at);
