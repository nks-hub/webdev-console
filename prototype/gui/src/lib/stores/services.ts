import { invoke } from '@tauri-apps/api/core';
import { listen, type UnlistenFn } from '@tauri-apps/api/event';

// ------------------------------------------------------------------ //
// Types                                                               //
// ------------------------------------------------------------------ //

export type ServiceStatus = 'running' | 'stopped' | 'starting' | 'stopping' | 'warning' | 'unknown';

export interface ServiceMetrics {
  cpu: number;       // percent 0-100
  memory: number;    // bytes
  connections?: number;
}

export interface Service {
  id: string;
  name: string;
  displayName: string;
  version: string;
  status: ServiceStatus;
  pid?: number;
  port?: number;
  metrics: ServiceMetrics;
  uptime?: number;   // seconds
  error?: string;
}

export interface DaemonEvent {
  event: string;
  id: string;
  timestamp: string;
  data: Record<string, unknown>;
}

// ------------------------------------------------------------------ //
// Runes-based reactive state                                          //
// ------------------------------------------------------------------ //

let _services = $state<Service[]>([
  {
    id: 'apache',
    name: 'apache',
    displayName: 'Apache',
    version: '2.4.62',
    status: 'unknown',
    port: 80,
    metrics: { cpu: 0, memory: 0 },
  },
  {
    id: 'mysql',
    name: 'mysql',
    displayName: 'MySQL',
    version: '8.0.40',
    status: 'unknown',
    port: 3306,
    metrics: { cpu: 0, memory: 0 },
  },
  {
    id: 'nginx',
    name: 'nginx',
    displayName: 'Nginx',
    version: '1.27.3',
    status: 'unknown',
    port: 443,
    metrics: { cpu: 0, memory: 0 },
  },
]);

let _loading = $state<Record<string, boolean>>({});
let _connected = $state(false);
let _lastUpdate = $state<Date | null>(null);
let _error = $state<string | null>(null);

let _unlisteners: UnlistenFn[] = [];

// ------------------------------------------------------------------ //
// Derived state                                                       //
// ------------------------------------------------------------------ //

export const services = {
  get list() { return _services; },
  get loading() { return _loading; },
  get connected() { return _connected; },
  get lastUpdate() { return _lastUpdate; },
  get error() { return _error; },

  get allRunning() {
    return _services.every(s => s.status === 'running');
  },

  get anyRunning() {
    return _services.some(s => s.status === 'running');
  },

  get runningCount() {
    return _services.filter(s => s.status === 'running').length;
  },

  find(id: string): Service | undefined {
    return _services.find(s => s.id === id);
  },
};

// ------------------------------------------------------------------ //
// Daemon communication                                                //
// ------------------------------------------------------------------ //

function setServiceStatus(id: string, status: ServiceStatus, extra?: Partial<Service>): void {
  const idx = _services.findIndex(s => s.id === id);
  if (idx !== -1) {
    _services[idx] = { ..._services[idx]!, status, ...extra };
  }
}

function setLoading(id: string, loading: boolean): void {
  _loading = { ..._loading, [id]: loading };
}

// ------------------------------------------------------------------ //
// Actions                                                             //
// ------------------------------------------------------------------ //

export async function startService(id: string): Promise<void> {
  setLoading(id, true);
  setServiceStatus(id, 'starting');
  try {
    await invoke<void>('start_service', { name: id });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    setServiceStatus(id, 'stopped', { error: message });
    _error = `Failed to start ${id}: ${message}`;
  } finally {
    setLoading(id, false);
  }
}

export async function stopService(id: string): Promise<void> {
  setLoading(id, true);
  setServiceStatus(id, 'stopping');
  try {
    await invoke<void>('stop_service', { name: id });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    setServiceStatus(id, 'running', { error: message });
    _error = `Failed to stop ${id}: ${message}`;
  } finally {
    setLoading(id, false);
  }
}

export async function restartService(id: string): Promise<void> {
  setLoading(id, true);
  setServiceStatus(id, 'stopping');
  try {
    await invoke<void>('restart_service', { name: id });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    _error = `Failed to restart ${id}: ${message}`;
  } finally {
    setLoading(id, false);
  }
}

export async function startAll(): Promise<void> {
  await Promise.all(_services.map(s => startService(s.id)));
}

export async function stopAll(): Promise<void> {
  await Promise.all(
    _services.filter(s => s.status === 'running').map(s => stopService(s.id))
  );
}

export async function refreshStatus(): Promise<void> {
  try {
    const statuses = await invoke<Service[]>('get_service_status');
    for (const updated of statuses) {
      const idx = _services.findIndex(s => s.id === updated.id);
      if (idx !== -1) {
        _services[idx] = { ..._services[idx]!, ...updated };
      }
    }
    _lastUpdate = new Date();
    _connected = true;
    _error = null;
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    _connected = false;
    _error = `Daemon unreachable: ${message}`;
  }
}

// ------------------------------------------------------------------ //
// Real-time event subscription                                        //
// ------------------------------------------------------------------ //

export async function connectToDaemon(): Promise<void> {
  // Subscribe to service status events from Tauri Rust backend
  const unlistenStatus = await listen<DaemonEvent>('service:status', (event) => {
    const { data } = event.payload;
    if (typeof data['name'] === 'string' && typeof data['status'] === 'string') {
      setServiceStatus(
        data['name'],
        data['status'] as ServiceStatus,
        {
          pid: typeof data['pid'] === 'number' ? data['pid'] : undefined,
          uptime: typeof data['uptime'] === 'number' ? data['uptime'] : undefined,
          metrics: (data['metrics'] as ServiceMetrics) ?? { cpu: 0, memory: 0 },
        }
      );
    }
    _lastUpdate = new Date();
    _connected = true;
  });

  // Subscribe to metrics events (high-frequency, ~1/sec per service)
  const unlistenMetrics = await listen<DaemonEvent>('service:metrics', (event) => {
    const { data } = event.payload;
    if (typeof data['name'] === 'string' && data['metrics']) {
      const idx = _services.findIndex(s => s.id === data['name']);
      if (idx !== -1) {
        _services[idx] = {
          ..._services[idx]!,
          metrics: data['metrics'] as ServiceMetrics,
        };
      }
    }
  });

  // Daemon connection state
  const unlistenConnected = await listen<{ connected: boolean }>('daemon:connected', (event) => {
    _connected = event.payload.connected;
    if (event.payload.connected) {
      void refreshStatus();
    }
  });

  _unlisteners = [unlistenStatus, unlistenMetrics, unlistenConnected];

  // Initial status fetch
  await refreshStatus();
}

export function disconnectFromDaemon(): void {
  for (const unlisten of _unlisteners) {
    unlisten();
  }
  _unlisteners = [];
  _connected = false;
}
