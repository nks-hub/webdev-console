use anyhow::{anyhow, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;
use tauri::{AppHandle, Emitter};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tracing::{debug, error, info, warn};

// Named pipe path — must match what the Go daemon opens
#[cfg(windows)]
const PIPE_NAME: &str = r"\\.\pipe\devforge-daemon";

#[cfg(unix)]
const PIPE_NAME: &str = "/tmp/devforge-daemon.sock";

const WATCHDOG_INTERVAL_SECS: u64 = 3;
const REQUEST_TIMEOUT_SECS: u64 = 10;

// ------------------------------------------------------------------ //
// JSON-RPC types                                                      //
// ------------------------------------------------------------------ //

#[derive(Serialize)]
struct JsonRpcRequest {
    jsonrpc: &'static str,
    id: u64,
    method: String,
    params: Value,
}

#[derive(Deserialize, Debug)]
struct JsonRpcResponse {
    #[allow(dead_code)]
    jsonrpc: String,
    #[allow(dead_code)]
    id: Option<u64>,
    result: Option<Value>,
    error: Option<JsonRpcError>,
}

#[derive(Deserialize, Debug)]
struct JsonRpcError {
    code: i64,
    message: String,
}

// ------------------------------------------------------------------ //
// Daemon client                                                       //
// ------------------------------------------------------------------ //

pub struct DaemonClient<'a> {
    app: &'a AppHandle,
}

impl<'a> DaemonClient<'a> {
    pub fn new(app: &'a AppHandle) -> Self {
        Self { app }
    }

    /// Send a JSON-RPC 2.0 request to the Go daemon over named pipe.
    pub async fn call(&self, method: &str, params: Value) -> Result<Value> {
        let request = JsonRpcRequest {
            jsonrpc: "2.0",
            id: 1,
            method: method.to_string(),
            params,
        };

        let payload = serde_json::to_vec(&request)?;

        let result = tokio::time::timeout(
            Duration::from_secs(REQUEST_TIMEOUT_SECS),
            send_request(payload),
        )
        .await
        .map_err(|_| anyhow!("daemon request timed out after {}s", REQUEST_TIMEOUT_SECS))??;

        Ok(result)
    }
}

#[cfg(windows)]
async fn send_request(payload: Vec<u8>) -> Result<Value> {
    use tokio::net::windows::named_pipe::ClientOptions;

    let mut client = ClientOptions::new()
        .open(PIPE_NAME)
        .map_err(|e| anyhow!("cannot connect to daemon pipe: {}", e))?;

    // Write length-prefixed frame (4-byte LE u32) then payload
    let len = payload.len() as u32;
    client.write_all(&len.to_le_bytes()).await?;
    client.write_all(&payload).await?;

    // Read response length
    let mut len_buf = [0u8; 4];
    client.read_exact(&mut len_buf).await?;
    let resp_len = u32::from_le_bytes(len_buf) as usize;

    // Read response payload
    let mut resp_buf = vec![0u8; resp_len];
    client.read_exact(&mut resp_buf).await?;

    let response: JsonRpcResponse = serde_json::from_slice(&resp_buf)?;

    if let Some(err) = response.error {
        return Err(anyhow!("daemon error {}: {}", err.code, err.message));
    }

    response.result.ok_or_else(|| anyhow!("empty result from daemon"))
}

#[cfg(unix)]
async fn send_request(payload: Vec<u8>) -> Result<Value> {
    use tokio::net::UnixStream;

    let mut stream = UnixStream::connect(PIPE_NAME)
        .await
        .map_err(|e| anyhow!("cannot connect to daemon socket: {}", e))?;

    let len = payload.len() as u32;
    stream.write_all(&len.to_le_bytes()).await?;
    stream.write_all(&payload).await?;

    let mut len_buf = [0u8; 4];
    stream.read_exact(&mut len_buf).await?;
    let resp_len = u32::from_le_bytes(len_buf) as usize;

    let mut resp_buf = vec![0u8; resp_len];
    stream.read_exact(&mut resp_buf).await?;

    let response: JsonRpcResponse = serde_json::from_slice(&resp_buf)?;

    if let Some(err) = response.error {
        return Err(anyhow!("daemon error {}: {}", err.code, err.message));
    }

    response.result.ok_or_else(|| anyhow!("empty result from daemon"))
}

// ------------------------------------------------------------------ //
// Connection watchdog                                                 //
// ------------------------------------------------------------------ //

/// Periodically checks whether the daemon is reachable and emits
/// `daemon:connected` events to the frontend.
pub async fn start_connection_watchdog(app: AppHandle) {
    let was_connected = Arc::new(AtomicBool::new(false));
    let mut interval = tokio::time::interval(Duration::from_secs(WATCHDOG_INTERVAL_SECS));

    info!("daemon connection watchdog started");

    loop {
        interval.tick().await;

        let ping_result = tokio::time::timeout(
            Duration::from_secs(2),
            send_request(build_ping_payload()),
        )
        .await;

        let connected = ping_result.is_ok() && ping_result.unwrap().is_ok();
        let prev = was_connected.swap(connected, Ordering::Relaxed);

        if connected != prev {
            debug!("daemon connection changed: {}", connected);
            let _ = app.emit("daemon:connected", serde_json::json!({ "connected": connected }));
        }

        if !connected {
            warn!("daemon not reachable at {}", PIPE_NAME);
        }
    }
}

fn build_ping_payload() -> Vec<u8> {
    let request = JsonRpcRequest {
        jsonrpc: "2.0",
        id: 0,
        method: "system.ping".to_string(),
        params: Value::Null,
    };
    let payload = serde_json::to_vec(&request).unwrap_or_default();
    let len = payload.len() as u32;
    let mut frame = len.to_le_bytes().to_vec();
    frame.extend_from_slice(&payload);
    frame
}
