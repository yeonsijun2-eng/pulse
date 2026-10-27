interface D1Result<T = unknown> {
  results: T[];
  meta: { changes: number };
}

interface D1Statement {
  bind(...values: unknown[]): D1Statement;
  first<T = Record<string, unknown>>(): Promise<T | null>;
  all<T = Record<string, unknown>>(): Promise<D1Result<T>>;
  run(): Promise<D1Result>;
}

interface D1Database {
  prepare(query: string): D1Statement;
  batch(statements: D1Statement[]): Promise<D1Result[]>;
}

interface Env {
  DB: D1Database;
  PULSE_ADMIN_PASSWORD: string;
  PULSE_ALLOWED_ORIGIN: string;
}

interface LicensePayload {
  version: number;
  deviceId: string;
  type: "seven_day" | "thirty_day" | "permanent";
  issuedAt: number;
  expiresAt: number;
}

interface LicenseRequest {
  id: string;
  device_id: string;
  status: "pending" | "awaiting_signature" | "issued";
  license_type: LicensePayload["type"] | null;
  license_code: string | null;
  created_at: number;
  issued_at: number | null;
}

const RETENTION_SECONDS = 180 * 24 * 60 * 60;
const SESSION_SECONDS = 30 * 60;
const ACCOUNT_SESSION_SECONDS = 14 * 24 * 60 * 60;
const PASSWORD_ITERATIONS = 210_000;
const PUBLIC_WINDOW_SECONDS = 30 * 60;
const ADMIN_WINDOW_SECONDS = 15 * 60;
const MAX_BODY_BYTES = 4096;
const LICENSE_PREFIX = "PULSE1";
const PUBLIC_KEY_SPKI = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEAfkuoqdkbck2BWStPVv83VL06m9vFMHgs7C+H80Wj2TdlQS5YzKU2kKjWDQNeMnyFtwflPhAnw2M3g/x+0DNZQ==";
const encoder = new TextEncoder();

function jsonResponse(body: unknown, status: number, cors: Record<string, string>): Response {
  const headers = new Headers(cors);
  headers.set("Content-Type", "application/json; charset=utf-8");
  headers.set("Cache-Control", "no-store");
  return new Response(JSON.stringify(body), {
    status,
    headers,
  });
}

function corsHeaders(request: Request, allowedOrigin: string): Record<string, string> | null {
  const origin = request.headers.get("Origin");
  if (origin && origin !== allowedOrigin) return null;
  return {
    ...(origin ? { "Access-Control-Allow-Origin": allowedOrigin, Vary: "Origin" } : {}),
    "Access-Control-Allow-Methods": "GET, POST, PATCH, DELETE, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type, Authorization, X-Request-Token",
    "Access-Control-Max-Age": "600",
  };
}

function base64UrlEncode(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function base64UrlDecode(value: string): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]+$/.test(value)) return null;
  try {
    const base64 = value.replaceAll("-", "+").replaceAll("_", "/");
    const binary = atob(base64 + "=".repeat((4 - base64.length % 4) % 4));
    return Uint8Array.from(binary, character => character.charCodeAt(0));
  } catch {
    return null;
  }
}

async function sha256(value: string | Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", typeof value === "string" ? encoder.encode(value) : value));
}

async function derivePasswordHash(password: string, salt: Uint8Array): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey("raw", encoder.encode(password), "PBKDF2", false, ["deriveBits"]);
  return new Uint8Array(await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt, iterations: PASSWORD_ITERATIONS },
    key,
    256,
  ));
}

function safeEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) difference |= left[index] ^ right[index];
  return difference === 0;
}

async function keyedIpHash(ip: string, password: string): Promise<string> {
  const key = await crypto.subtle.importKey("raw", encoder.encode(password), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const digest = new Uint8Array(await crypto.subtle.sign("HMAC", key, encoder.encode(ip)));
  return base64UrlEncode(digest);
}

async function parseBody(request: Request): Promise<Record<string, unknown> | null> {
  const declaredSize = Number(request.headers.get("Content-Length") ?? 0);
  if (declaredSize > MAX_BODY_BYTES) return null;
  const body = await request.text();
  if (encoder.encode(body).byteLength > MAX_BODY_BYTES) return null;
  try {
    const value: unknown = JSON.parse(body);
    return value !== null && typeof value === "object" && !Array.isArray(value)
      ? value as Record<string, unknown>
      : null;
  } catch {
    return null;
  }
}

function isConfigured(env: Env): boolean {
  return typeof env.PULSE_ADMIN_PASSWORD === "string" && env.PULSE_ADMIN_PASSWORD.length >= 24
    && typeof env.PULSE_ALLOWED_ORIGIN === "string" && env.PULSE_ALLOWED_ORIGIN.startsWith("https://");
}

async function applyRateLimit(
  env: Env,
  request: Request,
  bucketPrefix: string,
  limit: number,
  windowSeconds: number,
): Promise<boolean> {
  const ipHash = await keyedIpHash(request.headers.get("CF-Connecting-IP") ?? "unknown", env.PULSE_ADMIN_PASSWORD);
  const bucket = `${bucketPrefix}:${ipHash}`;
  const now = Math.floor(Date.now() / 1000);
  await env.DB.prepare("DELETE FROM rate_limits WHERE window_ends_at <= ?").bind(now).run();
  await env.DB.prepare(
    `INSERT INTO rate_limits (bucket, hit_count, window_ends_at)
     VALUES (?, 1, ?)
     ON CONFLICT(bucket) DO UPDATE SET
       hit_count = CASE WHEN window_ends_at <= ? THEN 1 ELSE hit_count + 1 END,
       window_ends_at = CASE WHEN window_ends_at <= ? THEN ? ELSE window_ends_at END`,
  ).bind(bucket, now + windowSeconds, now, now, now + windowSeconds).run();
  const row = await env.DB.prepare("SELECT hit_count FROM rate_limits WHERE bucket = ?")
    .bind(bucket)
    .first<{ hit_count: number }>();
  return (row?.hit_count ?? 0) <= limit;
}

async function authorizeAdmin(request: Request, env: Env): Promise<boolean> {
  const header = request.headers.get("Authorization") ?? "";
  const match = /^Bearer ([A-Za-z0-9_-]{32,128})$/.exec(header);
  if (!match) return false;
  const tokenHash = base64UrlEncode(await sha256(match[1]));
  const now = Math.floor(Date.now() / 1000);
  const session = await env.DB.prepare("SELECT expires_at FROM admin_sessions WHERE token_hash = ?")
    .bind(tokenHash)
    .first<{ expires_at: number }>();
  if (!session) return false;
  if (session.expires_at <= now) {
    await env.DB.prepare("DELETE FROM admin_sessions WHERE token_hash = ?").bind(tokenHash).run();
    return false;
  }
  return true;
}

async function validateLicenseCode(code: string, expectedDeviceId: string): Promise<LicensePayload | null> {
  if (code.length > 1024) return null;
  const parts = code.trim().split(".");
  if (parts.length !== 3 || parts[0] !== LICENSE_PREFIX) return null;
  const payloadBytes = base64UrlDecode(parts[1]);
  const signature = base64UrlDecode(parts[2]);
  if (!payloadBytes || !signature || signature.length !== 64) return null;

  let payload: LicensePayload;
  try {
    payload = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(payloadBytes)) as LicensePayload;
  } catch {
    return null;
  }
  const now = Math.floor(Date.now() / 1000);
  if (payload.version !== 1 || typeof payload.deviceId !== "string"
      || !/^[A-F0-9]{32}$/.test(payload.deviceId) || payload.deviceId !== expectedDeviceId
      || !["seven_day", "thirty_day", "permanent"].includes(payload.type)
      || !Number.isSafeInteger(payload.issuedAt) || payload.issuedAt <= 0 || payload.issuedAt > now + 300
      || !Number.isSafeInteger(payload.expiresAt)) return null;

  const expectedExpiry = payload.type === "permanent"
    ? 0
    : payload.issuedAt + (payload.type === "seven_day" ? 7 : 30) * 24 * 60 * 60;
  if (payload.expiresAt !== expectedExpiry
      || (payload.type !== "permanent" && now >= payload.expiresAt)) return null;

  const publicKeyBytes = Uint8Array.from(atob(PUBLIC_KEY_SPKI), character => character.charCodeAt(0));
  const publicKey = await crypto.subtle.importKey(
    "spki",
    publicKeyBytes,
    { name: "ECDSA", namedCurve: "P-256" },
    false,
    ["verify"],
  );
  const valid = await crypto.subtle.verify(
    { name: "ECDSA", hash: "SHA-256" },
    publicKey,
    signature,
    payloadBytes,
  );
  return valid ? payload : null;
}

function requestSummary(request: LicenseRequest) {
  return {
    id: request.id,
    deviceId: request.device_id,
    status: request.status,
    licenseType: request.license_type,
    createdAt: new Date(request.created_at * 1000).toISOString(),
    issuedAt: request.issued_at === null ? null : new Date(request.issued_at * 1000).toISOString(),
  };
}

interface AccountRow {
  id: string;
  name: string;
  email: string;
  created_at: number;
}

interface AccountSession extends AccountRow {
  expires_at: number;
}

function accountSummary(account: AccountRow) {
  return {
    id: account.id,
    name: account.name,
    email: account.email,
    status: "active",
    createdAt: new Date(account.created_at * 1000).toISOString(),
  };
}

function logAccountActivity(
  env: Env,
  accountId: string,
  type: string,
  label: string,
  timestamp: number,
): D1Statement {
  return env.DB.prepare(
    "INSERT INTO account_activity (account_id, type, label, status, created_at) VALUES (?, ?, ?, 'RECORDED', ?)",
  ).bind(accountId, type, label, timestamp);
}

async function authenticateAccount(request: Request, env: Env, now: number): Promise<AccountSession | null> {
  const match = /^Bearer ([A-Za-z0-9_-]{43})$/.exec(request.headers.get("Authorization") ?? "");
  if (!match) return null;
  const tokenHash = base64UrlEncode(await sha256(match[1]));
  const session = await env.DB.prepare(
    `SELECT accounts.id, accounts.name, accounts.email, accounts.created_at, account_sessions.expires_at
     FROM account_sessions JOIN accounts ON accounts.id = account_sessions.account_id
     WHERE account_sessions.token_hash = ?`,
  ).bind(tokenHash).first<AccountSession>();
  if (!session) return null;
  if (session.expires_at <= now) {
    await env.DB.prepare("DELETE FROM account_sessions WHERE token_hash = ?").bind(tokenHash).run();
    return null;
  }
  return session;
}

async function createAccountSession(env: Env, accountId: string, now: number): Promise<string> {
  const token = base64UrlEncode(crypto.getRandomValues(new Uint8Array(32)));
  await env.DB.prepare(
    "INSERT INTO account_sessions (token_hash, account_id, created_at, expires_at) VALUES (?, ?, ?, ?)",
  ).bind(base64UrlEncode(await sha256(token)), accountId, now, now + ACCOUNT_SESSION_SECONDS).run();
  return token;
}

function validEmail(value: string): boolean {
  return value.length <= 254 && /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(value);
}

async function handleAccountRequest(
  request: Request,
  env: Env,
  path: string,
  url: URL,
  cors: Record<string, string>,
  now: number,
): Promise<Response | null> {
  if (!path.startsWith("/api/account/")) return null;

  if (request.method === "GET" && path === "/api/account/status") {
    return jsonResponse({
      available: true,
      emailVerification: false,
      passwordRecovery: false,
    }, 200, cors);
  }

  if (request.method === "POST" && (path === "/api/account/register" || path === "/api/account/login")) {
    const isRegister = path.endsWith("/register");
    const rateLimitKey = isRegister ? "account-register" : "account-login";
    if (!await applyRateLimit(env, request, rateLimitKey, isRegister ? 5 : 10, 15 * 60)) {
      return jsonResponse({ error: "Too many attempts. Please try again later." }, 429, cors);
    }

    const body = await parseBody(request);
    const email = typeof body?.email === "string" ? body.email.trim().toLowerCase() : "";
    const password = typeof body?.password === "string" ? body.password : "";
    if (!validEmail(email) || password.length < 12 || encoder.encode(password).byteLength > 128) {
      return jsonResponse({ error: "Enter a valid email address and a password between 12 and 128 bytes." }, 400, cors);
    }

    if (isRegister) {
      const name = typeof body?.name === "string" ? body.name.trim() : "";
      if (!name || name.length > 80 || /[\u0000-\u001f\u007f]/.test(name)) {
        return jsonResponse({ error: "Your name must be between 1 and 80 characters." }, 400, cors);
      }
      const existing = await env.DB.prepare("SELECT id FROM accounts WHERE email = ?").bind(email).first<{ id: string }>();
      if (existing) return jsonResponse({ error: "An account with this email already exists." }, 409, cors);

      const id = crypto.randomUUID();
      const salt = crypto.getRandomValues(new Uint8Array(16));
      const passwordHash = await derivePasswordHash(password, salt);
      try {
        await env.DB.batch([
          env.DB.prepare(
            "INSERT INTO accounts (id, name, email, password_salt, password_hash, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
          ).bind(id, name, email, base64UrlEncode(salt), base64UrlEncode(passwordHash), now, now),
          env.DB.prepare("INSERT INTO account_settings (account_id) VALUES (?)").bind(id),
          logAccountActivity(env, id, "account_created", "Account created", now),
        ]);
      } catch (error) {
        if (error instanceof Error && /UNIQUE constraint failed: accounts\.email/i.test(error.message)) {
          return jsonResponse({ error: "An account with this email already exists." }, 409, cors);
        }
        throw error;
      }
      const token = await createAccountSession(env, id, now);
      return jsonResponse({ token, expiresInSeconds: ACCOUNT_SESSION_SECONDS }, 201, cors);
    }

    const account = await env.DB.prepare(
      "SELECT id, name, email, created_at, password_salt, password_hash FROM accounts WHERE email = ?",
    ).bind(email).first<AccountRow & { password_salt: string; password_hash: string }>();
    const salt = account ? base64UrlDecode(account.password_salt) : null;
    const expectedHash = account ? base64UrlDecode(account.password_hash) : null;
    const calculatedHash = await derivePasswordHash(password, salt ?? new Uint8Array(16));
    if (!account || !salt || !expectedHash || !safeEqual(calculatedHash, expectedHash)) {
      return jsonResponse({ error: "Email or password is incorrect." }, 401, cors);
    }
    const token = await createAccountSession(env, account.id, now);
    await env.DB.prepare(
      "INSERT INTO account_activity (account_id, type, label, status, created_at) VALUES (?, 'login', 'Signed in', 'RECORDED', ?)",
    ).bind(account.id, now).run();
    return jsonResponse({ token, expiresInSeconds: ACCOUNT_SESSION_SECONDS }, 200, cors);
  }

  const account = await authenticateAccount(request, env, now);
  if (!account) return jsonResponse({ error: "Your account session has expired. Sign in again." }, 401, cors);

  if (request.method === "GET" && path === "/api/account/me") {
    return jsonResponse(accountSummary(account), 200, cors);
  }

  if (request.method === "POST" && path === "/api/account/logout") {
    const match = /^Bearer ([A-Za-z0-9_-]{43})$/.exec(request.headers.get("Authorization") ?? "");
    if (match) {
      await env.DB.batch([
        env.DB.prepare("DELETE FROM account_sessions WHERE token_hash = ?").bind(base64UrlEncode(await sha256(match[1]))),
        logAccountActivity(env, account.id, "logout", "Signed out", now),
      ]);
    }
    return new Response(null, { status: 204, headers: cors });
  }

  if (request.method === "GET" && path === "/api/account/devices") {
    const { results } = await env.DB.prepare(
      "SELECT id, device_id, name, registered_at, last_seen_at FROM account_devices WHERE account_id = ? ORDER BY registered_at DESC LIMIT 100",
    ).bind(account.id).all<{
      id: string;
      device_id: string;
      name: string;
      registered_at: number;
      last_seen_at: number;
    }>();
    return jsonResponse({
      devices: results.map(device => ({
        id: device.id,
        deviceId: device.device_id,
        name: device.name,
        registeredAt: new Date(device.registered_at * 1000).toISOString(),
        lastSeenAt: new Date(device.last_seen_at * 1000).toISOString(),
        status: "REGISTERED",
      })),
    }, 200, cors);
  }

  if (request.method === "POST" && path === "/api/account/devices") {
    const body = await parseBody(request);
    const deviceId = typeof body?.deviceId === "string" ? body.deviceId.trim().toUpperCase() : "";
    if (!/^[A-F0-9]{32}$/.test(deviceId)) {
      return jsonResponse({ error: "Enter the 32-character hexadecimal Device ID shown in PULSE." }, 400, cors);
    }
    const deviceCount = await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM account_devices WHERE account_id = ?",
    ).bind(account.id).first<{ count: number }>();
    if ((deviceCount?.count ?? 0) >= 10) {
      return jsonResponse({ error: "An account can register up to 10 devices." }, 409, cors);
    }
    const id = crypto.randomUUID();
    try {
      await env.DB.batch([
        env.DB.prepare(
          "INSERT INTO account_devices (id, account_id, device_id, name, registered_at, last_seen_at) VALUES (?, ?, ?, 'PULSE Device', ?, ?)",
        ).bind(id, account.id, deviceId, now, now),
        logAccountActivity(env, account.id, "device_registered", "Device registered", now),
      ]);
    } catch (error) {
      if (error instanceof Error && /UNIQUE constraint failed: account_devices\.device_id/i.test(error.message)) {
        return jsonResponse({ error: "This Device ID is already registered to an account." }, 409, cors);
      }
      throw error;
    }
    return jsonResponse({ id, deviceId, name: "PULSE Device", registeredAt: new Date(now * 1000).toISOString() }, 201, cors);
  }

  const deviceRoute = /^\/api\/account\/devices\/([a-f0-9-]{36})$/i.exec(path);
  if (deviceRoute && request.method === "PATCH") {
    const body = await parseBody(request);
    const name = typeof body?.name === "string" ? body.name.trim() : "";
    if (!name || name.length > 64 || /[\u0000-\u001f\u007f]/.test(name)) {
      return jsonResponse({ error: "Device names must be between 1 and 64 characters." }, 400, cors);
    }
    const owned = await env.DB.prepare(
      "SELECT id FROM account_devices WHERE id = ? AND account_id = ?",
    ).bind(deviceRoute[1], account.id).first<{ id: string }>();
    if (!owned) return jsonResponse({ error: "The device was not found." }, 404, cors);
    await env.DB.batch([
      env.DB.prepare("UPDATE account_devices SET name = ? WHERE id = ? AND account_id = ?").bind(name, deviceRoute[1], account.id),
      logAccountActivity(env, account.id, "device_renamed", "Device name updated", now),
    ]);
    return jsonResponse({ id: deviceRoute[1], name }, 200, cors);
  }

  if (deviceRoute && request.method === "DELETE") {
    const owned = await env.DB.prepare(
      "SELECT id FROM account_devices WHERE id = ? AND account_id = ?",
    ).bind(deviceRoute[1], account.id).first<{ id: string }>();
    if (!owned) return jsonResponse({ error: "The device was not found." }, 404, cors);
    await env.DB.batch([
      env.DB.prepare("DELETE FROM account_devices WHERE id = ? AND account_id = ?").bind(deviceRoute[1], account.id),
      logAccountActivity(env, account.id, "device_removed", "Device removed", now),
    ]);
    return new Response(null, { status: 204, headers: cors });
  }

  if (request.method === "GET" && path === "/api/account/activity") {
    const requestedLimit = Number(url.searchParams.get("limit") ?? 20);
    const limit = Number.isInteger(requestedLimit) ? Math.min(Math.max(requestedLimit, 1), 50) : 20;
    const cursorValue = url.searchParams.get("cursor");
    const cursor = cursorValue === null ? null : Number(cursorValue);
    if (cursorValue !== null && (!Number.isSafeInteger(cursor) || cursor! < 1)) {
      return jsonResponse({ error: "The activity cursor is invalid." }, 400, cors);
    }
    const query = cursor === null
      ? "SELECT id, type, label, status, created_at FROM account_activity WHERE account_id = ? ORDER BY id DESC LIMIT ?"
      : "SELECT id, type, label, status, created_at FROM account_activity WHERE account_id = ? AND id < ? ORDER BY id DESC LIMIT ?";
    const { results } = await env.DB.prepare(query)
      .bind(...(cursor === null ? [account.id, limit + 1] : [account.id, cursor, limit + 1]))
      .all<{ id: number; type: string; label: string; status: string; created_at: number }>();
    const hasMore = results.length > limit;
    const page = results.slice(0, limit);
    return jsonResponse({
      items: page.map(item => ({
        type: item.type,
        label: item.label,
        status: item.status,
        createdAt: new Date(item.created_at * 1000).toISOString(),
      })),
      nextCursor: hasMore && page.length ? String(page[page.length - 1].id) : null,
    }, 200, cors);
  }

  if (request.method === "PATCH" && path === "/api/account/profile") {
    const body = await parseBody(request);
    const name = typeof body?.name === "string" ? body.name.trim() : "";
    if (!name || name.length > 80 || /[\u0000-\u001f\u007f]/.test(name)) {
      return jsonResponse({ error: "Your name must be between 1 and 80 characters." }, 400, cors);
    }
    await env.DB.batch([
      env.DB.prepare("UPDATE accounts SET name = ?, updated_at = ? WHERE id = ?").bind(name, now, account.id),
      logAccountActivity(env, account.id, "profile_updated", "Profile updated", now),
    ]);
    return jsonResponse({ name }, 200, cors);
  }

  if (request.method === "PATCH" && path === "/api/account/settings") {
    const body = await parseBody(request);
    if (!body || (body.language !== undefined && body.language !== "en" && body.language !== "ko")
        || (body.notifications !== undefined && typeof body.notifications !== "boolean")
        || (body.reduceMotion !== undefined && typeof body.reduceMotion !== "boolean")) {
      return jsonResponse({ error: "The account settings are invalid." }, 400, cors);
    }
    const current = await env.DB.prepare(
      "SELECT language, notifications, reduce_motion FROM account_settings WHERE account_id = ?",
    ).bind(account.id).first<{ language: string; notifications: number; reduce_motion: number }>();
    const settings = {
      language: body.language ?? current?.language ?? "en",
      notifications: body.notifications ?? Boolean(current?.notifications),
      reduceMotion: body.reduceMotion ?? Boolean(current?.reduce_motion),
    };
    await env.DB.prepare(
      `INSERT INTO account_settings (account_id, language, notifications, reduce_motion)
       VALUES (?, ?, ?, ?)
       ON CONFLICT(account_id) DO UPDATE SET
         language = excluded.language,
         notifications = excluded.notifications,
         reduce_motion = excluded.reduce_motion`,
    ).bind(account.id, settings.language, Number(settings.notifications), Number(settings.reduceMotion)).run();
    return jsonResponse(settings, 200, cors);
  }

  return jsonResponse({ error: "Account endpoint not found." }, 404, cors);
}

async function handleRequest(request: Request, env: Env): Promise<Response> {
  const cors = corsHeaders(request, env.PULSE_ALLOWED_ORIGIN);
  if (!cors) return new Response("Origin not allowed", { status: 403 });
  if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: cors });
  if (request.method === "GET" && new URL(request.url).pathname === "/healthz") {
    try {
      await env.DB.prepare("SELECT 1").first();
      return jsonResponse({ status: "ok" }, 200, cors);
    } catch {
      return jsonResponse({ error: "License service storage is unavailable." }, 503, cors);
    }
  }
  if (!isConfigured(env)) return jsonResponse({ error: "License service is not configured." }, 503, cors);

  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, "") || "/";
  const now = Math.floor(Date.now() / 1000);

  const accountResponse = await handleAccountRequest(request, env, path, url, cors, now);
  if (accountResponse) return accountResponse;

  if (request.method === "POST" && path === "/api/license-requests") {
    if (!await applyRateLimit(env, request, "public", 5, PUBLIC_WINDOW_SECONDS)) {
      return jsonResponse({ error: "Too many requests. Please try again later." }, 429, cors);
    }
    const body = await parseBody(request);
    const deviceId = typeof body?.deviceId === "string" ? body.deviceId.trim().toUpperCase() : "";
    if (!/^[A-F0-9]{32}$/.test(deviceId)) {
      return jsonResponse({ error: "Enter the 32-character device ID shown in PULSE." }, 400, cors);
    }
    await env.DB.prepare("DELETE FROM license_requests WHERE created_at <= ?").bind(now - RETENTION_SECONDS).run();
    const id = crypto.randomUUID().replaceAll("-", "");
    const requestToken = base64UrlEncode(crypto.getRandomValues(new Uint8Array(32)));
    await env.DB.prepare(
      "INSERT INTO license_requests (id, device_id, request_token_hash, status, created_at) VALUES (?, ?, ?, 'pending', ?)",
    ).bind(id, deviceId, base64UrlEncode(await sha256(requestToken)), now).run();
    await env.DB.prepare(
      "DELETE FROM license_requests WHERE id IN (SELECT id FROM license_requests ORDER BY created_at DESC LIMIT -1 OFFSET 5000)",
    ).run();
    return jsonResponse({ requestId: id, requestToken, status: "pending", createdAt: new Date(now * 1000).toISOString() }, 201, cors);
  }

  const userStatus = /^\/api\/license-requests\/([a-f0-9]{32})$/.exec(path);
  const download = /^\/api\/license-requests\/([a-f0-9]{32})\/download$/.exec(path);
  if ((request.method === "GET" && userStatus) || (request.method === "GET" && download)) {
    const id = (userStatus ?? download)![1];
    const token = request.headers.get("X-Request-Token") ?? "";
    if (token.length < 32 || token.length > 128) return jsonResponse({ error: "The request receipt is invalid." }, 404, cors);
    const row = await env.DB.prepare(
      "SELECT id, device_id, status, license_type, license_code, created_at, issued_at, request_token_hash FROM license_requests WHERE id = ?",
    ).bind(id).first<LicenseRequest & { request_token_hash: string }>();
    const storedTokenHash = row ? base64UrlDecode(row.request_token_hash) : null;
    if (!row || !storedTokenHash || !safeEqual(await sha256(token), storedTokenHash)) {
      return jsonResponse({ error: "The request receipt was not found." }, 404, cors);
    }
    if (download) {
      if (row.status !== "issued" || !row.license_code) {
        return jsonResponse({ error: "This license is not ready to download yet." }, 409, cors);
      }
      return new Response(`${row.license_code}\n`, {
        status: 200,
        headers: {
          ...cors,
          "Content-Type": "application/vnd.pulse.license",
          "Content-Disposition": `attachment; filename="PULSE-${id.slice(0, 8)}.pulse-license"`,
          "Cache-Control": "no-store",
        },
      });
    }
    return jsonResponse({
      status: row.status,
      createdAt: new Date(row.created_at * 1000).toISOString(),
      licenseType: row.license_type,
    }, 200, cors);
  }

  if (request.method === "POST" && path === "/api/admin/login") {
    if (!await applyRateLimit(env, request, "admin", 5, ADMIN_WINDOW_SECONDS)) {
      return jsonResponse({ error: "Too many login attempts. Please try again later." }, 429, cors);
    }
    const body = await parseBody(request);
    const password = typeof body?.password === "string" ? body.password : "";
    if (!safeEqual(await sha256(password), await sha256(env.PULSE_ADMIN_PASSWORD))) {
      return jsonResponse({ error: "The admin password is incorrect." }, 401, cors);
    }
    const token = base64UrlEncode(crypto.getRandomValues(new Uint8Array(32)));
    const tokenHash = base64UrlEncode(await sha256(token));
    await env.DB.prepare("DELETE FROM admin_sessions WHERE expires_at <= ?").bind(now).run();
    await env.DB.prepare("INSERT INTO admin_sessions (token_hash, expires_at) VALUES (?, ?)")
      .bind(tokenHash, now + SESSION_SECONDS).run();
    await env.DB.prepare("DELETE FROM rate_limits WHERE window_ends_at <= ?").bind(now).run();
    return jsonResponse({ token, expiresInSeconds: SESSION_SECONDS }, 200, cors);
  }

  const adminCollection = path === "/api/admin/requests";
  const adminPrepare = /^\/api\/admin\/requests\/([a-f0-9]{32})\/prepare$/.exec(path);
  const adminLicense = /^\/api\/admin\/requests\/([a-f0-9]{32})\/license$/.exec(path);
  const adminDelete = /^\/api\/admin\/requests\/([a-f0-9]{32})$/.exec(path);
  if ((request.method === "GET" && adminCollection)
      || (request.method === "POST" && (adminPrepare || adminLicense))
      || (request.method === "DELETE" && adminDelete)) {
    if (!await authorizeAdmin(request, env)) return jsonResponse({ error: "Admin session expired. Sign in again." }, 401, cors);

    if (adminCollection) {
      const { results } = await env.DB.prepare(
        "SELECT id, device_id, status, license_type, created_at, issued_at FROM license_requests ORDER BY created_at DESC LIMIT 5000",
      ).all<LicenseRequest>();
      return jsonResponse(results.map(requestSummary), 200, cors);
    }

    const id = (adminPrepare ?? adminLicense ?? adminDelete)![1];
    if (request.method === "DELETE") {
      const result = await env.DB.prepare("DELETE FROM license_requests WHERE id = ?").bind(id).run();
      return result.meta.changes ? new Response(null, { status: 204, headers: cors })
        : jsonResponse({ error: "The request was not found." }, 404, cors);
    }

    const body = await parseBody(request);
    if (adminPrepare) {
      const licenseType = body?.licenseType;
      if (licenseType !== "seven_day" && licenseType !== "thirty_day" && licenseType !== "permanent") {
        return jsonResponse({ error: "Choose a supported license term." }, 400, cors);
      }
      const result = await env.DB.prepare(
        "UPDATE license_requests SET status = 'awaiting_signature', license_type = ? WHERE id = ? AND status <> 'issued'",
      ).bind(licenseType, id).run();
      if (!result.meta.changes) return jsonResponse({ error: "The request was not found or was already issued." }, 404, cors);
      const prepared = await env.DB.prepare(
        "SELECT id, device_id, status, license_type, license_code, created_at, issued_at FROM license_requests WHERE id = ?",
      ).bind(id).first<LicenseRequest>();
      return jsonResponse(requestSummary(prepared!), 200, cors);
    }

    const licenseCode = typeof body?.licenseCode === "string" ? body.licenseCode.trim() : "";
    if (!licenseCode || licenseCode.length > 1024) {
      return jsonResponse({ error: "Provide a valid signed PULSE license code." }, 400, cors);
    }
    const record = await env.DB.prepare(
      "SELECT id, device_id, status, license_type FROM license_requests WHERE id = ?",
    ).bind(id).first<Pick<LicenseRequest, "id" | "device_id" | "status" | "license_type">>();
    if (!record || record.status !== "awaiting_signature") {
      return jsonResponse({ error: "Prepare this request before uploading a license." }, 409, cors);
    }
    const payload = await validateLicenseCode(licenseCode, record.device_id);
    if (!payload) return jsonResponse({ error: "The license signature, device ID, or expiry is invalid." }, 400, cors);
    if (payload.type !== record.license_type) {
      return jsonResponse({ error: "The signed license term does not match the selected term." }, 400, cors);
    }
    const result = await env.DB.prepare(
      "UPDATE license_requests SET status = 'issued', license_code = ?, issued_at = ? WHERE id = ? AND status = 'awaiting_signature' AND device_id = ? AND license_type = ?",
    ).bind(licenseCode, now, id, record.device_id, record.license_type).run();
    return result.meta.changes
      ? jsonResponse({ status: "issued" }, 200, cors)
      : jsonResponse({ error: "The request changed before the license could be saved. Refresh and retry." }, 409, cors);
  }

  return jsonResponse({ error: "Not found." }, 404, cors);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      return await handleRequest(request, env);
    } catch (error) {
      console.error("PULSE license API request failed.", error instanceof Error ? error.message : "unknown error");
      const cors = corsHeaders(request, env.PULSE_ALLOWED_ORIGIN) ?? {};
      return jsonResponse({ error: "The license service could not complete this request." }, 500, cors);
    }
  },
};
