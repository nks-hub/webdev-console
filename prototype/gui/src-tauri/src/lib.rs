mod commands;
mod daemon;
mod tray;

use tauri::Manager;
use tracing_subscriber::EnvFilter;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::from_default_env().add_directive("wdc_gui=debug".parse().unwrap()))
        .init();

    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_os::init())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_tray::init())
        .invoke_handler(tauri::generate_handler![
            commands::start_service,
            commands::stop_service,
            commands::restart_service,
            commands::get_service_status,
            commands::open_url,
            commands::open_terminal,
        ])
        .setup(|app| {
            // Set up system tray
            tray::setup_tray(app)?;

            // Show main window after setup
            if let Some(window) = app.get_webview_window("main") {
                window.show()?;
            }

            // Start daemon connection watchdog in background
            let app_handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                daemon::start_connection_watchdog(app_handle).await;
            });

            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running NKS WebDev Console GUI");
}
