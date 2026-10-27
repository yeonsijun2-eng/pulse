use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine as _};
use chrono::{DateTime, Utc};
use rusqlite::{params, Connection, OptionalExtension};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    path::{Path, PathBuf},
    process::Command,
    sync::Mutex,
    time::{Duration, Instant},
};
use sysinfo::{Disks, System};
use tauri::{Manager, State};
use windows::Win32::{
    Security::Cryptography::{
        BCryptCloseAlgorithmProvider, BCryptDestroyKey, BCryptGenRandom, BCryptImportKeyPair,
        BCryptOpenAlgorithmProvider, BCryptVerifySignature, BCRYPT_ALG_HANDLE,
        BCRYPT_ECCPUBLIC_BLOB, BCRYPT_ECDSA_P256_ALGORITHM, BCRYPT_ECDSA_PUBLIC_P256_MAGIC,
        BCRYPT_FLAGS, BCRYPT_KEY_HANDLE, BCRYPT_OPEN_ALGORITHM_PROVIDER_FLAGS,
        BCRYPT_USE_SYSTEM_PREFERRED_RNG,
    },
    UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL},
};

const LICENSE_WEBSITE: &str = "https://pulse.xyz.ci";
const PUBLIC_KEY_PEM: &str = include_str!("../../LicensingCore/pulse-license-public-key.pem");
const MAX_LICENSE_SIZE: u64 = 1024;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LicensePayload {
    version: u8,
    device_id: String,
    #[serde(rename = "type")]
    license_type: String,
    issued_at: i64,
    expires_at: i64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LicenseStatus {
    device_id: String,
    state: String,
    license_type: Option<String>,
    expires_at: Option<DateTime<Utc>>,
}

struct AppState {
    db: Mutex<Connection>,
    system: Mutex<System>,
    disks: Mutex<Disks>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct Metrics {
    cpu_percent: f32,
    memory_used: u64,
    memory_total: u64,
    memory_percent: f32,
    uptime_seconds: u64,
    disk_used: u64,
    disk_total: u64,
    gpu_status: String,
    sampled_at: DateTime<Utc>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LibraryApp {
    id: i64,
    name: String,
    path: String,
    arguments: String,
    category: String,
    favorite: bool,
    total_minutes: i64,
    last_launched: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct NewApp {
    name: String,
    path: String,
    arguments: String,
    category: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct Activity {
    id: i64,
    kind: String,
    detail: String,
    occurred_at: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct Scene {
    id: i64,
    name: String,
    description: String,
    active: bool,
}

fn open_database(app: &tauri::AppHandle) -> Result<Connection, String> {
    let path = app
        .path()
        .app_local_data_dir()
        .map_err(|error| error.to_string())?;
    std::fs::create_dir_all(&path).map_err(|error| error.to_string())?;
    let db = Connection::open(path.join("pulse.sqlite3")).map_err(|error| error.to_string())?;
    db.execute_batch(
        "PRAGMA journal_mode = WAL;
         PRAGMA foreign_keys = ON;
         CREATE TABLE IF NOT EXISTS library_apps (
             id INTEGER PRIMARY KEY,
             name TEXT NOT NULL,
             path TEXT NOT NULL UNIQUE,
             arguments TEXT NOT NULL DEFAULT '',
             category TEXT NOT NULL DEFAULT 'Application',
             favorite INTEGER NOT NULL DEFAULT 0,
             total_minutes INTEGER NOT NULL DEFAULT 0,
             last_launched TEXT
         );
         CREATE TABLE IF NOT EXISTS activity (
             id INTEGER PRIMARY KEY,
             kind TEXT NOT NULL,
             detail TEXT NOT NULL,
             occurred_at TEXT NOT NULL
         );
         CREATE TABLE IF NOT EXISTS scenes (
             id INTEGER PRIMARY KEY,
             name TEXT NOT NULL UNIQUE,
             description TEXT NOT NULL DEFAULT '',
             active INTEGER NOT NULL DEFAULT 0
         );
         CREATE TABLE IF NOT EXISTS blackbox_sessions (
             id INTEGER PRIMARY KEY,
             started_at TEXT NOT NULL,
             ended_at TEXT,
             note TEXT NOT NULL DEFAULT ''
         );
         CREATE TABLE IF NOT EXISTS blackbox_samples (
             id INTEGER PRIMARY KEY,
             session_id INTEGER NOT NULL REFERENCES blackbox_sessions(id) ON DELETE CASCADE,
             sampled_at TEXT NOT NULL,
             cpu_percent REAL NOT NULL,
             memory_used INTEGER NOT NULL,
             memory_total INTEGER NOT NULL
         );
         CREATE TABLE IF NOT EXISTS config_backups (
             id INTEGER PRIMARY KEY,
             source_path TEXT NOT NULL,
             backup_path TEXT NOT NULL,
             sha256 TEXT NOT NULL,
             created_at TEXT NOT NULL
         );
         CREATE TABLE IF NOT EXISTS device_license (
             id INTEGER PRIMARY KEY CHECK (id = 1),
             device_id TEXT NOT NULL,
             license_code TEXT
         );
         PRAGMA user_version = 2;",
    )
    .map_err(|error| error.to_string())?;
    let device_id = load_or_create_device_id(&db)?;
    db.execute(
        "INSERT OR IGNORE INTO device_license (id, device_id) VALUES (1, ?1)",
        [&device_id],
    )
    .map_err(|error| error.to_string())?;
    for (name, description) in [
        ("COMPETITIVE", "A focused, low-distraction setup."),
        ("CASUAL", "A balanced everyday setup."),
        ("STREAMING", "A profile for streaming sessions."),
        ("WORK", "A profile for focused work."),
        ("CUSTOM", "Your personal setup."),
    ] {
        db.execute(
            "INSERT OR IGNORE INTO scenes (name, description) VALUES (?1, ?2)",
            params![name, description],
        )
        .map_err(|error| error.to_string())?;
    }
    Ok(db)
}

fn load_or_create_device_id(db: &Connection) -> Result<String, String> {
    if let Some(device_id) = db
        .query_row(
            "SELECT device_id FROM device_license WHERE id = 1",
            [],
            |row| row.get::<_, String>(0),
        )
        .optional()
        .map_err(|error| error.to_string())?
    {
        return Ok(device_id);
    }

    let mut random = [0u8; 16];
    let status = unsafe { BCryptGenRandom(None, &mut random, BCRYPT_USE_SYSTEM_PREFERRED_RNG) };
    if status.0 != 0 {
        return Err(format!(
            "Windows could not create a device ID (CNG status 0x{:08X}).",
            status.0
        ));
    }

    random[6] = (random[6] & 0x0F) | 0x40;
    random[8] = (random[8] & 0x3F) | 0x80;
    Ok(random.iter().map(|byte| format!("{byte:02X}")).collect())
}

fn decode_public_key() -> Result<Vec<u8>, String> {
    let encoded = PUBLIC_KEY_PEM
        .lines()
        .filter(|line| !line.starts_with("-----"))
        .collect::<String>();
    let der = base64::engine::general_purpose::STANDARD
        .decode(encoded)
        .map_err(|_| "The embedded license public key is malformed.".to_owned())?;
    let point = der
        .get(der.len().saturating_sub(65)..)
        .filter(|point| point.first() == Some(&0x04))
        .ok_or_else(|| "The embedded license public key is not a P-256 key.".to_owned())?;

    let mut blob = Vec::with_capacity(72);
    blob.extend_from_slice(&BCRYPT_ECDSA_PUBLIC_P256_MAGIC.to_le_bytes());
    blob.extend_from_slice(&32u32.to_le_bytes());
    blob.extend_from_slice(&point[1..]);
    Ok(blob)
}

struct CngAlgorithm(BCRYPT_ALG_HANDLE);

impl Drop for CngAlgorithm {
    fn drop(&mut self) {
        unsafe {
            let _ = BCryptCloseAlgorithmProvider(self.0, 0);
        }
    }
}

struct CngKey(BCRYPT_KEY_HANDLE);

impl Drop for CngKey {
    fn drop(&mut self) {
        unsafe {
            let _ = BCryptDestroyKey(self.0);
        }
    }
}

fn verify_license_signature(payload: &[u8], signature: &[u8]) -> Result<bool, String> {
    if signature.len() != 64 {
        return Ok(false);
    }

    let blob = decode_public_key()?;
    let mut algorithm = BCRYPT_ALG_HANDLE(std::ptr::null_mut());
    let open_status = unsafe {
        BCryptOpenAlgorithmProvider(
            &mut algorithm,
            BCRYPT_ECDSA_P256_ALGORITHM,
            None,
            BCRYPT_OPEN_ALGORITHM_PROVIDER_FLAGS(0),
        )
    };
    if open_status.0 != 0 {
        return Err(format!(
            "Windows license verification could not start (CNG status 0x{:08X}).",
            open_status.0
        ));
    }
    let algorithm = CngAlgorithm(algorithm);

    let mut key = BCRYPT_KEY_HANDLE(std::ptr::null_mut());
    let import_status = unsafe {
        BCryptImportKeyPair(algorithm.0, None, BCRYPT_ECCPUBLIC_BLOB, &mut key, &blob, 0)
    };
    if import_status.0 != 0 {
        return Err(format!(
            "Windows could not load the PULSE license verification key (CNG status 0x{:08X}).",
            import_status.0
        ));
    }
    let key = CngKey(key);
    let digest = Sha256::digest(payload);
    let verify_status =
        unsafe { BCryptVerifySignature(key.0, None, &digest, signature, BCRYPT_FLAGS(0)) };
    Ok(verify_status.0 == 0)
}

fn validate_license_code(
    license_code: &str,
    expected_device_id: &str,
) -> Result<(LicensePayload, &'static str), String> {
    if license_code.len() > MAX_LICENSE_SIZE as usize {
        return Err("The license code is too long.".to_owned());
    }

    let mut parts = license_code.split('.');
    let (Some(prefix), Some(payload_part), Some(signature_part), None) =
        (parts.next(), parts.next(), parts.next(), parts.next())
    else {
        return Err("The license code is incomplete.".to_owned());
    };
    if prefix != "PULSE1" {
        return Err("This is not a PULSE license.".to_owned());
    }

    let payload_bytes = URL_SAFE_NO_PAD
        .decode(payload_part)
        .map_err(|_| "The license payload is malformed.".to_owned())?;
    let signature = URL_SAFE_NO_PAD
        .decode(signature_part)
        .map_err(|_| "The license signature is malformed.".to_owned())?;
    if !verify_license_signature(&payload_bytes, &signature)? {
        return Err("The license signature is invalid or has been modified.".to_owned());
    }

    let payload: LicensePayload = serde_json::from_slice(&payload_bytes)
        .map_err(|_| "The signed license information is invalid.".to_owned())?;
    if payload.version != 1
        || payload.device_id.len() != 32
        || !payload
            .device_id
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'A'..=b'F').contains(&byte))
        || payload.issued_at <= 0
        || payload.issued_at > Utc::now().timestamp().saturating_add(300)
        || !matches!(
            payload.license_type.as_str(),
            "seven_day" | "thirty_day" | "permanent"
        )
    {
        return Err("The signed license contains unsupported information.".to_owned());
    }

    if payload.device_id != expected_device_id {
        return Ok((payload, "deviceMismatch"));
    }

    let expected_expiry = match payload.license_type.as_str() {
        "seven_day" => payload.issued_at.checked_add(7 * 24 * 60 * 60),
        "thirty_day" => payload.issued_at.checked_add(30 * 24 * 60 * 60),
        "permanent" => Some(0),
        _ => None,
    };
    if expected_expiry != Some(payload.expires_at) {
        return Err("The license expiry does not match its signed term.".to_owned());
    }

    if payload.license_type != "permanent" && Utc::now().timestamp() >= payload.expires_at {
        return Ok((payload, "expired"));
    }

    Ok((payload, "active"))
}

fn status_for_device(db: &Connection) -> Result<LicenseStatus, String> {
    let (device_id, license_code) = db
        .query_row(
            "SELECT device_id, license_code FROM device_license WHERE id = 1",
            [],
            |row| Ok((row.get::<_, String>(0)?, row.get::<_, Option<String>>(1)?)),
        )
        .map_err(|error| error.to_string())?;

    let Some(license_code) = license_code else {
        return Ok(LicenseStatus {
            device_id,
            state: "notActivated".to_owned(),
            license_type: None,
            expires_at: None,
        });
    };

    match validate_license_code(&license_code, &device_id) {
        Ok((payload, state)) => Ok(LicenseStatus {
            device_id,
            state: state.to_owned(),
            license_type: Some(payload.license_type),
            expires_at: (payload.expires_at != 0)
                .then(|| DateTime::from_timestamp(payload.expires_at, 0))
                .flatten(),
        }),
        Err(_) => Ok(LicenseStatus {
            device_id,
            state: "invalid".to_owned(),
            license_type: None,
            expires_at: None,
        }),
    }
}

fn store_valid_license(db: &Connection, license_code: &str) -> Result<LicenseStatus, String> {
    let device_id = db
        .query_row(
            "SELECT device_id FROM device_license WHERE id = 1",
            [],
            |row| row.get::<_, String>(0),
        )
        .map_err(|error| error.to_string())?;
    let (payload, state) = validate_license_code(license_code.trim(), &device_id)?;
    if state == "deviceMismatch" {
        return Err("This license was issued for a different device.".to_owned());
    }
    if state == "expired" {
        return Err(
            "This license has expired. Request a new license from pulse.xyz.ci.".to_owned(),
        );
    }

    db.execute(
        "UPDATE device_license SET license_code = ?1 WHERE id = 1",
        [license_code.trim()],
    )
    .map_err(|error| error.to_string())?;
    record_activity(
        db,
        "LICENSE",
        &format!(
            "Activated {} license",
            payload.license_type.replace('_', " ")
        ),
    )?;
    status_for_device(db)
}

#[tauri::command]
fn get_license_status(state: State<'_, AppState>) -> Result<LicenseStatus, String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    status_for_device(&db)
}

#[tauri::command]
fn activate_license(
    license_code: String,
    state: State<'_, AppState>,
) -> Result<LicenseStatus, String> {
    if license_code.trim().len() > MAX_LICENSE_SIZE as usize {
        return Err("The license code is too long.".to_owned());
    }
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    store_valid_license(&db, &license_code)
}

#[tauri::command]
fn import_license_file(path: String, state: State<'_, AppState>) -> Result<LicenseStatus, String> {
    let selected_path = Path::new(&path)
        .canonicalize()
        .map_err(|error| format!("The selected license file could not be opened: {error}"))?;
    if !selected_path
        .extension()
        .is_some_and(|extension| extension.eq_ignore_ascii_case("pulse-license"))
    {
        return Err("Select a .pulse-license file downloaded from the PULSE website.".to_owned());
    }
    let metadata = std::fs::metadata(&selected_path)
        .map_err(|error| format!("The selected license file could not be read: {error}"))?;
    if !metadata.is_file() || metadata.len() > MAX_LICENSE_SIZE {
        return Err("The selected license file is empty or too large.".to_owned());
    }
    let license_code = std::fs::read_to_string(&selected_path)
        .map_err(|error| format!("The selected license file is not valid UTF-8: {error}"))?;
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    store_valid_license(&db, &license_code)
}

#[tauri::command]
fn remove_license(state: State<'_, AppState>) -> Result<LicenseStatus, String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    db.execute(
        "UPDATE device_license SET license_code = NULL WHERE id = 1",
        [],
    )
    .map_err(|error| error.to_string())?;
    record_activity(&db, "LICENSE", "Removed the local license")?;
    status_for_device(&db)
}

#[tauri::command]
fn open_license_website(device_id: String, state: State<'_, AppState>) -> Result<(), String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    let expected_device_id = db
        .query_row(
            "SELECT device_id FROM device_license WHERE id = 1",
            [],
            |row| row.get::<_, String>(0),
        )
        .map_err(|error| error.to_string())?;
    if device_id != expected_device_id {
        return Err("The device ID changed. Refresh the activation page and try again.".to_owned());
    }

    let request_url = format!("{LICENSE_WEBSITE}/#device={device_id}");
    let url = windows::core::HSTRING::from(request_url);
    let result = unsafe {
        ShellExecuteW(
            None,
            windows::core::w!("open"),
            &url,
            None,
            None,
            SW_SHOWNORMAL,
        )
    };
    if result.0 as isize <= 32 {
        return Err("Windows could not open the PULSE license website.".to_owned());
    }
    Ok(())
}
fn record_activity(db: &Connection, kind: &str, detail: &str) -> Result<(), String> {
    db.execute(
        "INSERT INTO activity (kind, detail, occurred_at) VALUES (?1, ?2, ?3)",
        params![kind, detail, Utc::now().to_rfc3339()],
    )
    .map_err(|error| error.to_string())?;
    Ok(())
}

fn safe_executable(path: &str) -> Result<PathBuf, String> {
    let executable = Path::new(path)
        .canonicalize()
        .map_err(|error| format!("The selected executable could not be opened: {error}"))?;
    if !executable.is_file()
        || !executable
            .extension()
            .is_some_and(|extension| extension.eq_ignore_ascii_case("exe"))
    {
        return Err("Choose an existing Windows .exe file.".to_owned());
    }
    Ok(executable)
}

#[tauri::command]
fn get_metrics(state: State<'_, AppState>) -> Result<Metrics, String> {
    let mut system = state
        .system
        .lock()
        .map_err(|_| "System monitor is unavailable.")?;
    system.refresh_cpu_all();
    system.refresh_memory();
    std::thread::sleep(Duration::from_millis(220));
    system.refresh_cpu_all();
    system.refresh_memory();
    let cpu_percent = system.global_cpu_usage();
    let memory_used = system.used_memory();
    let memory_total = system.total_memory();
    drop(system);

    let mut disks = state
        .disks
        .lock()
        .map_err(|_| "Disk monitor is unavailable.")?;
    disks.refresh(true);
    let (disk_used, disk_total) = disks.list().iter().fold((0, 0), |(used, total), disk| {
        let capacity = disk.total_space();
        (
            used + capacity.saturating_sub(disk.available_space()),
            total + capacity,
        )
    });

    Ok(Metrics {
        cpu_percent,
        memory_used,
        memory_total,
        memory_percent: if memory_total == 0 {
            0.0
        } else {
            memory_used as f32 * 100.0 / memory_total as f32
        },
        uptime_seconds: System::uptime(),
        disk_used,
        disk_total,
        gpu_status: "NOT AVAILABLE".to_owned(),
        sampled_at: Utc::now(),
    })
}

#[tauri::command]
fn list_library(state: State<'_, AppState>) -> Result<Vec<LibraryApp>, String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    let mut query = db
        .prepare("SELECT id, name, path, arguments, category, favorite, total_minutes, last_launched FROM library_apps ORDER BY favorite DESC, COALESCE(last_launched, '') DESC, id DESC")
        .map_err(|error| error.to_string())?;
    let rows = query
        .query_map([], |row| {
            Ok(LibraryApp {
                id: row.get(0)?,
                name: row.get(1)?,
                path: row.get(2)?,
                arguments: row.get(3)?,
                category: row.get(4)?,
                favorite: row.get::<_, i64>(5)? != 0,
                total_minutes: row.get(6)?,
                last_launched: row.get(7)?,
            })
        })
        .map_err(|error| error.to_string())?;
    rows.collect::<Result<Vec<_>, _>>()
        .map_err(|error| error.to_string())
}

#[tauri::command]
fn add_library_app(input: NewApp, state: State<'_, AppState>) -> Result<i64, String> {
    let path = safe_executable(&input.path)?;
    let name = input.name.trim();
    if name.is_empty() || name.len() > 100 {
        return Err("Enter an app name between 1 and 100 characters.".to_owned());
    }
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    db.execute(
        "INSERT INTO library_apps (name, path, arguments, category) VALUES (?1, ?2, ?3, ?4)",
        params![
            name,
            path.to_string_lossy(),
            input.arguments.trim(),
            input.category.trim()
        ],
    )
    .map_err(|error| {
        format!("Could not add this app. It may already be in your Library. {error}")
    })?;
    let id = db.last_insert_rowid();
    record_activity(&db, "LIBRARY", &format!("Added {name}"))?;
    Ok(id)
}

#[tauri::command]
fn toggle_favorite(id: i64, state: State<'_, AppState>) -> Result<(), String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    let changed = db
        .execute(
            "UPDATE library_apps SET favorite = 1 - favorite WHERE id = ?1",
            [id],
        )
        .map_err(|error| error.to_string())?;
    if changed == 0 {
        return Err("This Library item no longer exists.".to_owned());
    }
    Ok(())
}

#[tauri::command]
fn remove_library_app(id: i64, state: State<'_, AppState>) -> Result<(), String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    let name = db
        .query_row("SELECT name FROM library_apps WHERE id = ?1", [id], |row| {
            row.get::<_, String>(0)
        })
        .optional()
        .map_err(|error| error.to_string())?;
    if let Some(name) = name {
        db.execute("DELETE FROM library_apps WHERE id = ?1", [id])
            .map_err(|error| error.to_string())?;
        record_activity(&db, "LIBRARY", &format!("Removed {name}"))?;
    }
    Ok(())
}

#[tauri::command]
fn launch_library_app(
    id: i64,
    state: State<'_, AppState>,
    app: tauri::AppHandle,
) -> Result<(), String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    let (name, path, arguments) = db
        .query_row(
            "SELECT name, path, arguments FROM library_apps WHERE id = ?1",
            [id],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                ))
            },
        )
        .optional()
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "This Library item no longer exists.".to_owned())?;
    let executable = safe_executable(&path)?;
    let mut command = Command::new(executable);
    for argument in arguments.split_whitespace() {
        command.arg(argument);
    }
    let child = command
        .spawn()
        .map_err(|error| format!("Could not start {name}: {error}"))?;
    let started = Instant::now();
    let now = Utc::now().to_rfc3339();
    db.execute(
        "UPDATE library_apps SET last_launched = ?1 WHERE id = ?2",
        params![now, id],
    )
    .map_err(|error| error.to_string())?;
    record_activity(&db, "LAUNCH", &format!("Started {name}"))?;
    drop(db);

    std::thread::spawn(move || {
        let mut child = child;
        match child.wait() {
            Ok(status) => {
                let minutes = started.elapsed().as_secs().saturating_div(60) as i64;
                if let Ok(db) = app.state::<AppState>().db.lock() {
                    if db.execute(
                        "UPDATE library_apps SET total_minutes = total_minutes + ?1 WHERE id = ?2",
                        params![minutes, id],
                    ).is_err()
                    {
                        let _ = record_activity(&db, "ERROR", "Could not update program usage time.");
                    }
                    let result = if status.success() {
                        "closed"
                    } else {
                        "exited with an error"
                    };
                    let _ = record_activity(
                        &db,
                        "SESSION",
                        &format!("{name} {result} after {minutes} minutes."),
                    );
                }
            }
            Err(error) => {
                if let Ok(db) = app.state::<AppState>().db.lock() {
                    let _ = record_activity(
                        &db,
                        "ERROR",
                        &format!("Could not monitor {name}: {error}"),
                    );
                }
            }
        }
    });
    Ok(())
}

#[tauri::command]
fn list_activity(state: State<'_, AppState>) -> Result<Vec<Activity>, String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    let mut query = db
        .prepare("SELECT id, kind, detail, occurred_at FROM activity ORDER BY id DESC LIMIT 100")
        .map_err(|error| error.to_string())?;
    let rows = query
        .query_map([], |row| {
            Ok(Activity {
                id: row.get(0)?,
                kind: row.get(1)?,
                detail: row.get(2)?,
                occurred_at: row.get(3)?,
            })
        })
        .map_err(|error| error.to_string())?;
    rows.collect::<Result<Vec<_>, _>>()
        .map_err(|error| error.to_string())
}

#[tauri::command]
fn list_scenes(state: State<'_, AppState>) -> Result<Vec<Scene>, String> {
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    let mut query = db
        .prepare("SELECT id, name, description, active FROM scenes ORDER BY id")
        .map_err(|error| error.to_string())?;
    let rows = query
        .query_map([], |row| {
            Ok(Scene {
                id: row.get(0)?,
                name: row.get(1)?,
                description: row.get(2)?,
                active: row.get::<_, i64>(3)? != 0,
            })
        })
        .map_err(|error| error.to_string())?;
    rows.collect::<Result<Vec<_>, _>>()
        .map_err(|error| error.to_string())
}

#[tauri::command]
fn activate_scene(id: i64, state: State<'_, AppState>) -> Result<(), String> {
    let mut db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    let name = db
        .query_row("SELECT name FROM scenes WHERE id = ?1", [id], |row| {
            row.get::<_, String>(0)
        })
        .optional()
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "This Scene does not exist.".to_owned())?;
    let tx = db.transaction().map_err(|error| error.to_string())?;
    tx.execute("UPDATE scenes SET active = 0", [])
        .map_err(|error| error.to_string())?;
    tx.execute("UPDATE scenes SET active = 1 WHERE id = ?1", [id])
        .map_err(|error| error.to_string())?;
    record_activity(&tx, "SCENE", &format!("Selected {name}"))?;
    tx.commit().map_err(|error| error.to_string())
}

#[tauri::command]
fn create_scene(
    name: String,
    description: String,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let name = name.trim();
    let description = description.trim();
    if name.is_empty() || name.len() > 40 || description.len() > 240 {
        return Err(
            "Scene names must be 1–40 characters and descriptions up to 240 characters.".to_owned(),
        );
    }
    let db = state
        .db
        .lock()
        .map_err(|_| "Local database is unavailable.")?;
    db.execute(
        "INSERT INTO scenes (name, description) VALUES (?1, ?2)",
        params![name, description],
    )
    .map_err(|error| {
        format!("Could not create this Scene. A Scene with that name may already exist. {error}")
    })?;
    record_activity(&db, "SCENE", &format!("Created {name}"))
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let db = open_database(&app.handle()).map_err(std::io::Error::other)?;
            let mut system = System::new();
            system.refresh_cpu_all();
            system.refresh_memory();
            app.manage(AppState {
                db: Mutex::new(db),
                system: Mutex::new(system),
                disks: Mutex::new(Disks::new_with_refreshed_list()),
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_license_status,
            activate_license,
            import_license_file,
            remove_license,
            open_license_website,
            get_metrics,
            list_library,
            add_library_app,
            toggle_favorite,
            remove_library_app,
            launch_library_app,
            list_activity,
            list_scenes,
            activate_scene,
            create_scene
        ])
        .run(tauri::generate_context!())
        .expect("PULSE desktop runtime failed");
}
