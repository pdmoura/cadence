-- Cadence schema. Written for SQLite and Cloudflare D1 (D1 is SQLite-compatible):
-- no AUTOINCREMENT tricks, no unsupported pragmas, ISO-8601 text timestamps, positional params.

CREATE TABLE IF NOT EXISTS members (
  id          INTEGER PRIMARY KEY,
  name        TEXT    NOT NULL,
  email       TEXT    NOT NULL UNIQUE,
  role        TEXT    NOT NULL CHECK (role IN ('sdr', 'team_lead', 'developer')),
  timezone    TEXT    NOT NULL DEFAULT 'UTC',
  created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE TABLE IF NOT EXISTS leads (
  id            INTEGER PRIMARY KEY,
  company       TEXT    NOT NULL,
  website       TEXT,
  contact_name  TEXT,
  contact_title TEXT,
  email         TEXT,
  phone         TEXT,
  industry      TEXT,
  country       TEXT,
  employees     INTEGER,
  source        TEXT    NOT NULL DEFAULT 'manual',
  stage         TEXT    NOT NULL DEFAULT 'new'
                CHECK (stage IN ('new', 'researching', 'validated', 'contacted', 'qualified', 'disqualified')),
  owner_id      INTEGER REFERENCES members(id) ON DELETE SET NULL,
  score         INTEGER NOT NULL DEFAULT 0,
  notes         TEXT,
  created_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  updated_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE INDEX IF NOT EXISTS idx_leads_stage      ON leads (stage);
CREATE INDEX IF NOT EXISTS idx_leads_owner      ON leads (owner_id);
CREATE INDEX IF NOT EXISTS idx_leads_updated_at ON leads (updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_leads_company    ON leads (company COLLATE NOCASE);

-- Every stage change is recorded: the pipeline is auditable and reports are computed from events.
CREATE TABLE IF NOT EXISTS lead_stage_events (
  id          INTEGER PRIMARY KEY,
  lead_id     INTEGER NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
  from_stage  TEXT,
  to_stage    TEXT    NOT NULL,
  member_id   INTEGER REFERENCES members(id) ON DELETE SET NULL,
  reason      TEXT,
  created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE INDEX IF NOT EXISTS idx_stage_events_lead ON lead_stage_events (lead_id, created_at);
CREATE INDEX IF NOT EXISTS idx_stage_events_day  ON lead_stage_events (created_at);

-- AI / heuristic suggestions never change a lead directly: a person accepts or rejects each one.
CREATE TABLE IF NOT EXISTS enrichment_suggestions (
  id               INTEGER PRIMARY KEY,
  lead_id          INTEGER NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
  field            TEXT    NOT NULL,
  suggested_value  TEXT    NOT NULL,
  current_value    TEXT,
  source           TEXT    NOT NULL CHECK (source IN ('heuristic', 'llm', 'webhook')),
  confidence       REAL    NOT NULL DEFAULT 0.5,
  rationale        TEXT,
  status           TEXT    NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'accepted', 'rejected')),
  reviewed_by      INTEGER REFERENCES members(id) ON DELETE SET NULL,
  reviewed_at      TEXT,
  created_at       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE INDEX IF NOT EXISTS idx_suggestions_lead_status ON enrichment_suggestions (lead_id, status);
CREATE INDEX IF NOT EXISTS idx_suggestions_status      ON enrichment_suggestions (status, created_at);

CREATE TABLE IF NOT EXISTS activities (
  id           INTEGER PRIMARY KEY,
  lead_id      INTEGER NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
  member_id    INTEGER REFERENCES members(id) ON DELETE SET NULL,
  kind         TEXT    NOT NULL CHECK (kind IN ('call', 'email', 'linkedin', 'meeting', 'note')),
  summary      TEXT    NOT NULL,
  outcome      TEXT,
  occurred_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  created_at   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE INDEX IF NOT EXISTS idx_activities_lead ON activities (lead_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_activities_day  ON activities (occurred_at DESC);

-- One stand-up per member per day: the five questions the team answers every morning.
CREATE TABLE IF NOT EXISTS standups (
  id           INTEGER PRIMARY KEY,
  member_id    INTEGER NOT NULL REFERENCES members(id) ON DELETE CASCADE,
  day          TEXT    NOT NULL,
  yesterday    TEXT    NOT NULL DEFAULT '',
  today        TEXT    NOT NULL DEFAULT '',
  metric       TEXT    NOT NULL DEFAULT '',
  blockers     TEXT    NOT NULL DEFAULT '',
  help_needed  TEXT    NOT NULL DEFAULT '',
  created_at   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  updated_at   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  UNIQUE (member_id, day)
);

CREATE INDEX IF NOT EXISTS idx_standups_day ON standups (day DESC);

-- Inbound webhook log: every delivery is kept, including rejected signatures, for debugging.
CREATE TABLE IF NOT EXISTS webhook_deliveries (
  id               INTEGER PRIMARY KEY,
  source           TEXT    NOT NULL,
  event            TEXT    NOT NULL,
  payload          TEXT    NOT NULL,
  signature_valid  INTEGER NOT NULL DEFAULT 0,
  status           TEXT    NOT NULL CHECK (status IN ('accepted', 'rejected', 'error')),
  detail           TEXT,
  lead_id          INTEGER REFERENCES leads(id) ON DELETE SET NULL,
  received_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE INDEX IF NOT EXISTS idx_webhooks_received ON webhook_deliveries (received_at DESC);
