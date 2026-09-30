CREATE TABLE IF NOT EXISTS schema_meta (
  version integer PRIMARY KEY,
  applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS users (
  id uuid PRIMARY KEY,
  username text NOT NULL UNIQUE,
  password_hash text NOT NULL,
  role text NOT NULL CHECK (role IN ('admin', 'member')),
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS sessions (
  id_hash text PRIMARY KEY,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  csrf_token text NOT NULL,
  expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  last_seen_at timestamptz NOT NULL DEFAULT now(),
  ip_hash text,
  user_agent text
);
CREATE INDEX IF NOT EXISTS sessions_expiry_idx ON sessions(expires_at);

CREATE TABLE IF NOT EXISTS auth_throttles (
  key_hash text PRIMARY KEY,
  failures integer NOT NULL DEFAULT 0,
  window_started_at timestamptz NOT NULL DEFAULT now(),
  blocked_until timestamptz
);

CREATE TABLE IF NOT EXISTS mail_accounts (
  id uuid PRIMARY KEY,
  address text NOT NULL UNIQUE,
  display_name text NOT NULL DEFAULT '',
  provider text NOT NULL CHECK (provider IN ('gmail', 'outlook', 'qq', '163', 'custom')),
  imap_host text NOT NULL,
  imap_port integer NOT NULL DEFAULT 993 CHECK (imap_port BETWEEN 1 AND 65535),
  imap_secure boolean NOT NULL DEFAULT true,
  smtp_host text NOT NULL,
  smtp_port integer NOT NULL DEFAULT 465 CHECK (smtp_port BETWEEN 1 AND 65535),
  smtp_secure boolean NOT NULL DEFAULT true,
  credential_ciphertext text NOT NULL,
  credential_key_version integer NOT NULL DEFAULT 1,
  status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'ready', 'degraded', 'disabled')),
  capability_receive boolean,
  capability_send boolean,
  last_synced_at timestamptz,
  last_error_code text,
  last_error_summary text,
  created_by uuid NOT NULL REFERENCES users(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS mail_accounts_provider_idx ON mail_accounts(provider);
CREATE INDEX IF NOT EXISTS mail_accounts_status_idx ON mail_accounts(status);

CREATE TABLE IF NOT EXISTS mail_tasks (
  id uuid PRIMARY KEY,
  account_id uuid NOT NULL REFERENCES mail_accounts(id) ON DELETE CASCADE,
  kind text NOT NULL CHECK (kind IN ('validate', 'sync', 'send', 'content')),
  priority integer NOT NULL CHECK (priority IN (0, 100)),
  state text NOT NULL DEFAULT 'queued' CHECK (state IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')),
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  attempts integer NOT NULL DEFAULT 0,
  max_attempts integer NOT NULL DEFAULT 6 CHECK (max_attempts BETWEEN 1 AND 12),
  available_at timestamptz NOT NULL DEFAULT now(),
  lease_owner text,
  lease_expires_at timestamptz,
  last_error_code text,
  last_error_summary text,
  requested_by uuid NOT NULL REFERENCES users(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  started_at timestamptz,
  finished_at timestamptz,
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS mail_tasks_one_active_kind_per_account
  ON mail_tasks(account_id, kind) WHERE state IN ('queued', 'running') AND kind <> 'content';
CREATE INDEX IF NOT EXISTS mail_tasks_claim_idx
  ON mail_tasks(priority DESC, available_at, created_at) WHERE state = 'queued';
CREATE INDEX IF NOT EXISTS mail_tasks_state_idx ON mail_tasks(state, updated_at DESC);

CREATE TABLE IF NOT EXISTS provider_gates (
  provider text PRIMARY KEY,
  next_connect_at timestamptz NOT NULL DEFAULT now(),
  paused_until timestamptz,
  consecutive_failures integer NOT NULL DEFAULT 0,
  last_failure_code text,
  active_lease_owner text,
  active_lease_expires_at timestamptz,
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS messages (
  id uuid PRIMARY KEY,
  account_id uuid NOT NULL REFERENCES mail_accounts(id) ON DELETE CASCADE,
  provider_uid text NOT NULL,
  message_id text,
  folder text NOT NULL DEFAULT 'INBOX',
  sender_name text NOT NULL DEFAULT '',
  sender_address text NOT NULL DEFAULT '',
  recipients jsonb NOT NULL DEFAULT '[]'::jsonb,
  subject text NOT NULL DEFAULT '',
  text_body text NOT NULL DEFAULT '',
  html_body text,
  received_at timestamptz NOT NULL,
  has_attachments boolean NOT NULL DEFAULT false,
  size_bytes bigint NOT NULL DEFAULT 0,
  search_document tsvector GENERATED ALWAYS AS (
    to_tsvector('simple', coalesce(subject, '') || ' ' || coalesce(sender_address, '') || ' ' || coalesce(text_body, ''))
  ) STORED,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(account_id, folder, provider_uid)
);
CREATE INDEX IF NOT EXISTS messages_account_received_idx ON messages(account_id, received_at DESC);
CREATE INDEX IF NOT EXISTS messages_search_idx ON messages USING GIN(search_document);

CREATE TABLE IF NOT EXISTS attachments (
  id uuid PRIMARY KEY,
  message_id uuid NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
  provider_part_id text NOT NULL,
  filename text NOT NULL,
  content_type text NOT NULL,
  size_bytes bigint NOT NULL DEFAULT 0,
  content bytea,
  fetched_at timestamptz,
  UNIQUE(message_id, provider_part_id)
);

CREATE TABLE IF NOT EXISTS audit_events (
  id uuid PRIMARY KEY,
  actor_user_id uuid REFERENCES users(id) ON DELETE SET NULL,
  action text NOT NULL,
  target_type text NOT NULL,
  target_id text,
  outcome text NOT NULL CHECK (outcome IN ('success', 'denied', 'failure')),
  details jsonb NOT NULL DEFAULT '{}'::jsonb,
  ip_hash text,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS audit_events_created_idx ON audit_events(created_at DESC);
CREATE INDEX IF NOT EXISTS audit_events_target_idx ON audit_events(target_type, target_id);

INSERT INTO schema_meta(version) VALUES (1) ON CONFLICT (version) DO NOTHING;
