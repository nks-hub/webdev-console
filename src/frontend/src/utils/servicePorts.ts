import { ref } from 'vue'
import { fetchSettings } from '../api/daemon'

// Settings key and built-in default for the services that listen on a port.
const PORTS: Record<string, { key?: string; fallback: number }> = {
  apache: { key: 'ports.http', fallback: 80 },
  httpd: { key: 'ports.http', fallback: 80 },
  nginx: { key: 'ports.http', fallback: 80 },
  mysql: { key: 'ports.mysql', fallback: 3306 },
  mariadb: { key: 'ports.mysql', fallback: 3306 },
  postgresql: { key: 'ports.postgresql', fallback: 5432 },
  redis: { key: 'ports.redis', fallback: 6379 },
  mailpit: { key: 'ports.mailpitHttp', fallback: 8025 },
}

const configured = ref<Record<string, string>>({})

/** Reads the port overrides from the daemon settings. Call after (re)connecting. */
export async function loadServicePorts(): Promise<void> {
  try { configured.value = await fetchSettings() } catch { /* keep the defaults */ }
}

/** Port a service listens on, or null for services without one. */
export function servicePort(serviceId: string): number | null {
  const entry = PORTS[serviceId]
  if (!entry) return null
  const set = entry.key ? parseInt(configured.value[entry.key] ?? '', 10) : NaN
  return set > 0 ? set : entry.fallback
}
