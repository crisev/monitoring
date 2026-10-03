-- Monitor v2 initial schema.
-- All timestamps are epoch milliseconds from the server clock.
-- "day" columns are calendar dates (YYYY-MM-DD) in the configured time zone.

-- Single row holding the parent-editable settings as validated JSON.
CREATE TABLE settings (
  id          INTEGER PRIMARY KEY CHECK (id = 1),
  json        TEXT    NOT NULL,
  version     INTEGER NOT NULL,
  updated_at  INTEGER NOT NULL,
  updated_by  TEXT    NOT NULL
);

-- One row per calendar day once it has been "opened" (allowance granted, cap applied).
CREATE TABLE days (
  day                  TEXT    PRIMARY KEY,
  opened_at            INTEGER NOT NULL,
  screen_used_seconds  INTEGER NOT NULL DEFAULT 0
);

-- Every change to a time balance.
--   bucket 'game':   balance = SUM(seconds) over all rows (carries over between days).
--   bucket 'screen': today's extra screen time = SUM(seconds) WHERE day = today (does not carry over).
-- kind: daily_allowance | cap | grant | adjustment | usage
-- A game session owns exactly one 'usage' row, updated as its lease is extended or refunded.
CREATE TABLE ledger (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  bucket      TEXT    NOT NULL CHECK (bucket IN ('game', 'screen')),
  day         TEXT    NOT NULL,
  kind        TEXT    NOT NULL,
  seconds     INTEGER NOT NULL,
  session_id  TEXT,
  by          TEXT    NOT NULL,
  note        TEXT,
  created_at  INTEGER NOT NULL
);
CREATE INDEX ledger_bucket_day ON ledger (bucket, day);
CREATE UNIQUE INDEX ledger_session ON ledger (session_id) WHERE session_id IS NOT NULL;

CREATE TABLE devices (
  id                  TEXT    PRIMARY KEY,
  name                TEXT    NOT NULL,
  token_hash          TEXT    UNIQUE,
  enroll_code_hash    TEXT,
  enroll_expires_at   INTEGER,
  created_at          INTEGER NOT NULL,
  last_heartbeat_at   INTEGER,
  last_screen_active  INTEGER NOT NULL DEFAULT 0,
  silent_alerted_at   INTEGER,
  client_version      TEXT,
  revoked             INTEGER NOT NULL DEFAULT 0
);

-- Gaming sessions. paid_until is the end of the prepaid lease (already debited).
CREATE TABLE game_sessions (
  id          TEXT    PRIMARY KEY,
  device_id   TEXT    NOT NULL REFERENCES devices (id),
  day         TEXT    NOT NULL,
  started_at  INTEGER NOT NULL,
  paid_until  INTEGER NOT NULL,
  ended_at    INTEGER,
  end_reason  TEXT,
  ended_by    TEXT
);
-- At most one open session per device.
CREATE UNIQUE INDEX game_sessions_one_open ON game_sessions (device_id) WHERE ended_at IS NULL;

-- Audit / activity events (parent actions, device reports, session lifecycle).
CREATE TABLE events (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  at         INTEGER NOT NULL,
  device_id  TEXT,
  type       TEXT    NOT NULL,
  detail     TEXT,
  by         TEXT
);
CREATE INDEX events_at ON events (at);
