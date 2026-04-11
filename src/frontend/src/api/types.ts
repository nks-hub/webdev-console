// TypeScript types matching C# daemon models

export type ServiceStatus = 'running' | 'stopped' | 'starting' | 'stopping' | 'crashed' | 'disabled' | 'error'

export interface ServiceInfo {
  id: string
  name?: string
  displayName?: string
  status?: ServiceStatus
  state?: number // 0=Stopped, 1=Starting, 2=Running, 3=Stopping, 4=Crashed, 5=Disabled
  pid: number | null
  cpuPercent: number
  memoryBytes: number
  uptimeSeconds?: number
  uptime?: string | number
  version?: string
}

export interface StatusResponse {
  status: string
  version: string
  plugins: number
  uptime: number
}

/**
 * Per-site Cloudflare Tunnel exposure. Mirrors `SiteCloudflareConfig` in
 * `NKS.WebDevConsole.Core/Models/SiteConfig.cs` — keep the field names
 * in sync with the C# record (snake→camel is handled by
 * System.Text.Json's default camelCase serializer).
 */
export interface CloudflareSiteConfig {
  /** True when the site is actively routed through the tunnel. */
  enabled: boolean
  /** Public subdomain label, e.g. "blog" for blog.nks-dev.cz. */
  subdomain: string
  /** Cloudflare zone ID from /api/cloudflare/zones. */
  zoneId: string
  /** Zone apex cached for display (e.g. "nks-dev.cz"). */
  zoneName: string
  /** Local service URL fragment, e.g. "localhost:80". */
  localService: string
  /** HTTP protocol for the local service: "http" or "https". */
  protocol: 'http' | 'https'
}

export interface SiteInfo {
  domain: string
  documentRoot: string
  phpVersion: string
  sslEnabled: boolean
  httpPort: number
  httpsPort: number
  aliases: string[]
  framework?: string
  environment?: Record<string, string>
  /**
   * Optional Cloudflare Tunnel exposure. Null / undefined means the site
   * is only reachable locally. Present but `enabled=false` means it was
   * configured before but is currently dormant — the DNS CNAME has been
   * removed but the sub-config is kept so re-enabling is one click.
   */
  cloudflare?: CloudflareSiteConfig | null
}

export interface PhpVersion {
  version: string
  majorMinor?: string
  path: string
  isDefault: boolean
  extensionCount?: number
  activeSiteCount?: number
}

export interface DatabaseInfo {
  name: string
  sizeBytes: number
  siteAssociation?: string
}

export interface CertInfo {
  domain: string
  expiresAt: string
  status: 'valid' | 'expiring' | 'expired'
}

// Plugin panel schema — what C# plugins return from /api/plugins/{id}/ui
export type PanelType =
  | 'service-status-card'
  | 'version-switcher'
  | 'config-editor'
  | 'log-viewer'
  | 'metrics-chart'
  | 'custom'

export interface PanelDefinition {
  type: PanelType
  props: Record<string, unknown>
}

export interface PluginUiDefinition {
  id: string
  title: string
  icon: string
  category: 'database' | 'webserver' | 'language' | 'cache' | 'mail' | 'other'
  panels: PanelDefinition[]
  /** Path to custom JS bundle for advanced plugins (Approach B) */
  bundleUrl?: string
}

export interface PluginManifest {
  id: string
  name: string
  version: string
  type: 'service' | 'tool'
  enabled: boolean
  description?: string
  author?: string
  permissions: {
    network?: boolean
    filesystem?: string[]
    process?: boolean
    gui?: boolean
  }
  ui?: PluginUiDefinition
}

export interface BinaryRelease {
  app: string
  version: string
  /** Semantic major.minor (e.g. "8.4" for php 8.4.20) — used by version groupers */
  majorMinor?: string
  url: string
  /** Platform triple — daemon returns (os, arch) pair separately, this is cosmetic */
  os: string
  arch: string
  archiveType?: string
  source?: string
  /** Optional HTTP User-Agent override for sources that block the default */
  userAgent?: string | null
  /** Optional SHA-256 hex for integrity verification (currently unused in frontend) */
  sha256?: string
  /** Optional size in bytes (set by some generators, absent from upstream scrapers) */
  sizeBytes?: number
}

export interface InstalledBinary {
  app: string
  version: string
  path: string
  isDefault?: boolean
}

export interface BinaryCatalog {
  [app: string]: BinaryRelease[]
}

export interface ServiceLog {
  lines: string[]
  serviceId: string
}

export interface ValidationState {
  phase: 'idle' | 'validating' | 'passed' | 'failed'
  message: string
  error?: string
}

export interface ProgressUpdate {
  percent: number
  message: string
  done: boolean
  error?: string
}

export interface MetricsUpdate {
  serviceId: string
  cpu: number
  memory: number
  uptime?: number
}

export interface LogEntry {
  serviceId: string
  level: string
  message: string
  timestamp: string
}
