-- Workspace settings (key/value) and direction on the delivery log for outbound notifications.

CREATE TABLE IF NOT EXISTS settings (
  key         TEXT PRIMARY KEY,
  value       TEXT NOT NULL,
  updated_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

ALTER TABLE webhook_deliveries ADD COLUMN direction TEXT NOT NULL DEFAULT 'inbound';

CREATE INDEX IF NOT EXISTS idx_webhooks_direction ON webhook_deliveries (direction, received_at DESC);

-- Demo workspace. A fresh install can run the setup wizard at /setup to replace all of this.
INSERT OR IGNORE INTO settings (key, value) VALUES
  ('workspace_name', 'Northstar Growth'),
  ('product', 'Done-for-you outbound and appointment setting for service businesses.'),
  ('icp', 'Owner-led service businesses with 10 to 250 employees (clinics, trades, logistics, professional services) that rely on phone and email follow-up and lose leads to slow response.'),
  ('markets', 'United States, United Kingdom, Kenya, Brazil, Mexico'),
  ('target_daily_touches', '15'),
  ('target_weekly_validated', '6'),
  ('target_weekly_qualified', '3'),
  ('auto_enrich', '1'),
  ('auto_assign', '1'),
  ('ai_enabled', '1'),
  ('ai_provider', 'none'),
  ('ai_model', 'claude-opus-5'),
  ('outbound_url', ''),
  ('outbound_events', 'lead.created,lead.qualified'),
  ('onboarded', '1');
