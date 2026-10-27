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
