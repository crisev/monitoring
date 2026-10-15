-- Which apps and websites were used, and for how long.
-- One row per device, 5-minute slot (server time), mode, app, site and window title.
CREATE TABLE activity (
  device_id      TEXT    NOT NULL,
  day            TEXT    NOT NULL,             -- calendar day of the slot (configured time zone)
  slot           INTEGER NOT NULL,             -- start of the 5-minute slot, epoch ms
  mode           TEXT    NOT NULL CHECK (mode IN ('school', 'gaming')),
  app            TEXT    NOT NULL,             -- process name without .exe, e.g. 'msedge', 'Code'
  site           TEXT    NOT NULL DEFAULT '',  -- domain for browser tabs, '' otherwise
  title          TEXT    NOT NULL DEFAULT '',  -- window title
  fg_seconds     INTEGER NOT NULL DEFAULT 0,   -- seconds in the foreground
  audio_seconds  INTEGER NOT NULL DEFAULT 0,   -- seconds playing sound
  blocked        INTEGER NOT NULL DEFAULT 0,   -- times it was blocked/closed in School mode
  PRIMARY KEY (device_id, slot, mode, app, site, title)
);
CREATE INDEX activity_day ON activity (day);
