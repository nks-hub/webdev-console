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
  id: string
  domain: string
  docRoot: string
  phpVersion: string
  sslEnabled: boolean
  status: 'active' | 'inactive'
  framework?: string
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
