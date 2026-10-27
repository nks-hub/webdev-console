import type { McpGrantCreateBody } from '../../api/daemon'

/**
 * Trust profiles the onboarding panel offers, as sets of grants for every
 * AI client. The hub names the active profile by comparing the active
 * grants with these sets.
 */
export type McpProfileId = 'minimal' | 'balanced' | 'localDev' | 'permissive' | 'custom'

export interface McpProfileDefinition {
  id: McpProfileId
  grants: McpGrantCreateBody[]
}

// Strict is the default: with no grants every request that needs approval
// asks the operator. Every other profile is an explicit opt-in.
export const DEFAULT_PROFILE: McpProfileId = 'minimal'

const always = (kindPattern: string, targetPattern: string, grantedBy: string, note: string): McpGrantCreateBody => ({
  scopeType: 'always',
  scopeValue: null,
  kindPattern,
  targetPattern,
  grantedBy,
  note,
})

// Local development: services can be restarted and databases whose name
// ends in _dev can be dropped, queried or re-imported without a banner.
// Grant targets accept a '*' wildcard (matched case-insensitively).
export const DEV_DATABASE_PATTERN = '*_dev'
const localDevDatabaseGrant = (kind: string, what: string): McpGrantCreateBody =>
  always(kind, DEV_DATABASE_PATTERN, 'onboarding-local-dev', `Auto-approve ${what} on ${DEV_DATABASE_PATTERN} databases (local development)`)

export const MCP_PROFILES: McpProfileDefinition[] = [
  { id: 'minimal', grants: [] },
  {
    id: 'balanced',
    // Only deploy and rollback, which can be undone; restore, drops and
    // settings writes keep asking.
    grants: [
      always('deploy', '*', 'onboarding-balanced', 'Auto-approve deploy on any site (reversible)'),
      always('rollback', '*', 'onboarding-balanced', 'Auto-approve rollback on any site (reversible)'),
    ],
  },
  {
    id: 'localDev',
    grants: [
      always('service_restart', '*', 'onboarding-local-dev', 'Auto-approve stopping/restarting local services (local development)'),
      localDevDatabaseGrant('database_drop', 'drop'),
      localDevDatabaseGrant('database_query', 'data-changing SQL'),
      localDevDatabaseGrant('database_import', 'dump import'),
    ],
  },
  {
    id: 'permissive',
    grants: [always('*', '*', 'onboarding-permissive', 'Auto-approve everything — explicit operator opt-in')],
  },
  { id: 'custom', grants: [] },
]

/**
 * The profile whose grants are exactly the given ones: 'minimal' for none,
 * 'custom' when no profile fits.
 */
export function detectProfile(grants: Array<{ kindPattern?: string | null; targetPattern?: string | null }>): McpProfileId {
  const key = (g: { kindPattern?: string | null; targetPattern?: string | null }) =>
    `${g.kindPattern || '*'}|${g.targetPattern || '*'}`
  const active = new Set(grants.map(key))
  for (const profile of MCP_PROFILES) {
    if (profile.id === 'custom') continue
    const wanted = new Set(profile.grants.map(key))
    if (wanted.size === active.size && [...wanted].every((k) => active.has(k))) return profile.id
  }
  return 'custom'
}
