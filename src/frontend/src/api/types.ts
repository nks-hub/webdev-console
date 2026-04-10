// TypeScript types matching C# daemon models

export type ServiceStatus = 'running' | 'stopped' | 'starting' | 'error'

export interface ServiceInfo {
  id: string
  name: string
  status: ServiceStatus
  pid: number | null
  cpuPercent: number
  memoryBytes: number
  uptimeSeconds: number
  version?: string
}

export interface StatusResponse {
  status: string
  version: string
  plugins: number
  uptime: number
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
}

export interface PhpVersion {
  version: string
  path: string
  isDefault: boolean
  extensionCount: number
  activeSiteCount: number
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
  url: string
  size?: number
  sha256?: string
  platform?: string
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
