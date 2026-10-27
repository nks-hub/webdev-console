// Which tool modules the server registers, from the --modules argument.
//
// All 108 tools at once is more than some AI clients take: Cursor warns
// above 40 tools and may hide the rest, and every tool definition costs
// context in each request. --modules=essential keeps the everyday ones.

/** Tool modules in registration order. */
export const MODULE_NAMES = [
  'sites', 'services', 'system', 'databases', 'mysql', 'ssl', 'php', 'binaries', 'plugins',
  'backup', 'settings', 'cloudflare', 'deploy', 'confirmation', 'composer', 'node', 'hosts',
] as const

export type ModuleName = (typeof MODULE_NAMES)[number]

/** Named sets accepted next to module names. */
export const MODULE_PRESETS: Record<string, readonly ModuleName[]> = {
  // 39 tools: under Cursor's 40-tool warning.
  essential: ['system', 'sites', 'services', 'databases'],
}

/**
 * Modules to register for these command-line arguments: all of them
 * without --modules. confirmation is always on, because destructive tools
 * of any module need an approval requested through it. Unknown names are
 * reported in `unknown` and skipped.
 */
export function selectModules(argv: readonly string[]): { modules: Set<ModuleName>; unknown: string[] } {
  const index = argv.findIndex((a) => a === '--modules' || a.startsWith('--modules='))
  if (index < 0) return { modules: new Set(MODULE_NAMES), unknown: [] }
  const arg = argv[index]!
  const list = arg.includes('=') ? arg.slice(arg.indexOf('=') + 1) : (argv[index + 1] ?? '')
  const modules = new Set<ModuleName>(['confirmation'])
  const unknown: string[] = []
  for (const raw of list.split(',')) {
    const name = raw.trim().toLowerCase()
    if (!name) continue
    if (MODULE_PRESETS[name]) MODULE_PRESETS[name]!.forEach((m) => modules.add(m))
    else if ((MODULE_NAMES as readonly string[]).includes(name)) modules.add(name as ModuleName)
    else unknown.push(name)
  }
  return { modules, unknown }
}
