use serde::{Deserialize, Serialize};
use tauri::AppHandle;

use crate::daemon::DaemonClient;

// ------------------------------------------------------------------ //
// Shared types                                                        //
// ------------------------------------------------------------------ //

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ServiceMetrics {
    pub cpu: f64,
    pub memory: u64,
    pub connections: Option<u32>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ServiceInfo {
    pub id: String,
    pub name: String,
    #[serde(rename = "displayName")]
    pub display_name: String,
    pub version: String,
    pub status: String,
    pub pid: Option<u32>,
    pub port: Option<u16>,
    pub metrics: ServiceMetrics,
    pub uptime: Option<u64>,
    pub error: Option<String>,
}

// ------------------------------------------------------------------ //
// Tauri commands                                                      //
// ------------------------------------------------------------------ //

/// Start a named service via the Go daemon.
#[tauri::command]
pub async fn start_service(app: AppHandle, name: String) -> Result<(), String> {
    let client = DaemonClient::new(&app);
    client
        .call("service.start", serde_json::json!({ "name": name }))
        .await
        .map(|_| ())
        .map_err(|e| e.to_string())
}

/// Stop a named service via the Go daemon.
#[tauri::command]
pub async fn stop_service(app: AppHandle, name: String) -> Result<(), String> {
    let client = DaemonClient::new(&app);
    client
        .call("service.stop", serde_json::json!({ "name": name }))
        .await
        .map(|_| ())
        .map_err(|e| e.to_string())
}

/// Restart a named service via the Go daemon.
#[tauri::command]
pub async fn restart_service(app: AppHandle, name: String) -> Result<(), String> {
    let client = DaemonClient::new(&app);
    client
        .call("service.restart", serde_json::json!({ "name": name }))
        .await
        .map(|_| ())
        .map_err(|e| e.to_string())
}

/// Fetch current status for all services.
#[tauri::command]
pub async fn get_service_status(app: AppHandle) -> Result<Vec<ServiceInfo>, String> {
    let client = DaemonClient::new(&app);
    let result = client
        .call("service.status", serde_json::json!({}))
        .await
        .map_err(|e| e.to_string())?;

    let services: Vec<ServiceInfo> = serde_json::from_value(result["services"].clone())
        .map_err(|e| e.to_string())?;

    Ok(services)
}

/// Open a URL in the system's default browser.
#[tauri::command]
pub async fn open_url(app: AppHandle, url: String) -> Result<(), String> {
    use tauri_plugin_shell::ShellExt;
    app.shell()
        .open(&url, None)
        .map_err(|e| e.to_string())
}

/// Open a terminal emulator at the given working directory.
#[tauri::command]
pub async fn open_terminal(_app: AppHandle, cwd: String) -> Result<(), String> {
    // On Windows, open Windows Terminal (wt.exe) if available, fall back to cmd
    #[cfg(windows)]
    {
        use std::process::Command;
        Command::new("wt.exe")
            .args(["-d", &cwd])
            .spawn()
            .or_else(|_| {
                Command::new("cmd.exe")
                    .args(["/K", "cd /d", &cwd])
                    .spawn()
            })
            .map(|_| ())
            .map_err(|e| e.to_string())
    }

    #[cfg(target_os = "macos")]
    {
        use std::process::Command;
        Command::new("open")
            .args(["-a", "Terminal", &cwd])
            .spawn()
            .map(|_| ())
            .map_err(|e| e.to_string())
    }

    #[cfg(target_os = "linux")]
    {
        use std::process::Command;
        Command::new("xdg-terminal")
            .arg(&cwd)
            .spawn()
            .map(|_| ())
            .map_err(|e| e.to_string())
    }
}
