import { describe, expect, it } from 'vitest'
import { MODULE_NAMES, selectModules } from './modules.js'

describe('selectModules', () => {
  it('registers every module without --modules', () => {
    expect([...selectModules(['node', 'index.js']).modules]).toEqual([...MODULE_NAMES])
  })

  it('expands presets, accepts both argument forms and keeps confirmation', () => {
    expect([...selectModules(['--modules=essential']).modules].sort())
      .toEqual(['confirmation', 'databases', 'services', 'sites', 'system'])
    expect([...selectModules(['--modules', 'sites,php']).modules].sort())
      .toEqual(['confirmation', 'php', 'sites'])
  })

  it('reports unknown names instead of failing', () => {
    const { modules, unknown } = selectModules(['--modules=sites, Bogus'])
    expect([...modules].sort()).toEqual(['confirmation', 'sites'])
    expect(unknown).toEqual(['bogus'])
  })
})
