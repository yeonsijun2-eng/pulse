CREATE TABLE IF NOT EXISTS license_requests (
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    request_token_hash TEXT NOT NULL,
    status TEXT NOT NULL,
    license_type TEXT,
    license_code TEXT,
    created_at INTEGER NOT NULL,
    issued_at INTEGER,
    CHECK (status IN ('pending', 'awaiting_signature', 'issued')),
    CHECK (license_type IS NULL OR license_type IN ('seven_day', 'thirty_day', 'permanent'))
);

CREATE INDEX IF NOT EXISTS idx_license_requests_created
    ON license_requests(created_at DESC);

CREATE TABLE IF NOT EXISTS admin_sessions (
    token_hash TEXT PRIMARY KEY,
    expires_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS rate_limits (
    bucket TEXT PRIMARY KEY,
    hit_count INTEGER NOT NULL,
    window_ends_at INTEGER NOT NULL
);
