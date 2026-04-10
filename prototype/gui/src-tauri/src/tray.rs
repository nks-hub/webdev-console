use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Manager, Runtime,
};
use tracing::debug;

/// Set up the system tray icon and menu for NKS WebDev Console.
pub fn setup_tray<R: Runtime>(app: &tauri::App<R>) -> tauri::Result<()> {
    // Menu items
    let open = MenuItem::with_id(app, "open", "Open NKS WebDev Console", true, None::<&str>)?;
    let separator1 = PredefinedMenuItem::separator(app)?;
    let start_all = MenuItem::with_id(app, "start_all", "Start All Services", true, None::<&str>)?;
    let stop_all = MenuItem::with_id(app, "stop_all", "Stop All Services", true, None::<&str>)?;
    let separator2 = PredefinedMenuItem::separator(app)?;
    let quit = MenuItem::with_id(app, "quit", "Quit NKS WebDev Console", true, None::<&str>)?;

    let menu = Menu::with_items(app, &[
        &open,
        &separator1,
        &start_all,
        &stop_all,
        &separator2,
        &quit,
    ])?;

    TrayIconBuilder::new()
        .icon(app.default_window_icon().unwrap().clone())
        .menu(&menu)
        .tooltip("NKS WebDev Console")
        .show_menu_on_left_click(false)
        .on_menu_event(move |app, event| {
            debug!("tray menu event: {}", event.id.as_ref());

            match event.id.as_ref() {
                "open" => {
                    show_main_window(app);
                }
                "start_all" => {
                    let handle = app.clone();
                    tauri::async_runtime::spawn(async move {
                        let _ = crate::commands::start_service(handle, "apache".to_string()).await;
                        let _ = crate::commands::start_service(handle, "mysql".to_string()).await;
                    });
                }
                "stop_all" => {
                    let handle = app.clone();
                    tauri::async_runtime::spawn(async move {
                        let _ = crate::commands::stop_service(handle, "apache".to_string()).await;
                        let _ = crate::commands::stop_service(handle, "mysql".to_string()).await;
                    });
                }
                "quit" => {
                    debug!("quit requested from tray");
                    app.exit(0);
                }
                _ => {}
            }
        })
        .on_tray_icon_event(|tray, event| {
            // Left-click: show/focus the main window
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_main_window(tray.app_handle());
            }
        })
        .build(app)?;

    Ok(())
}

fn show_main_window<R: Runtime>(app: &impl Manager<R>) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}
