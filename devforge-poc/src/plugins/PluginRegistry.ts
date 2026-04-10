import type { Component } from 'vue'
import type { PanelType } from '../api/types'

// Maps schema panel type strings to Vue components
const componentRegistry = new Map<string, Component>()

/** Register a Vue component under a panel type name. */
export function registerPanelComponent(type: PanelType | string, component: Component): void {
  componentRegistry.set(type, component)
}

/** Resolve a panel type to its Vue component. Returns null if unknown. */
export function resolvePanelComponent(type: string): Component | null {
  return componentRegistry.get(type) ?? null
}

/** Map of dynamically loaded plugin bundle modules (Approach B). */
const bundleRegistry = new Map<string, Record<string, Component>>()

/**
 * Dynamically import a plugin JS bundle from a URL.
 * The bundle must export named Vue components.
 * Returns the module's exports so SchemaRenderer can use 'custom' panel type.
 */
export async function loadPluginBundle(pluginId: string, bundleUrl: string): Promise<void> {
  if (bundleRegistry.has(pluginId)) return
  try {
    const mod = await import(/* @vite-ignore */ bundleUrl) as Record<string, Component>
    bundleRegistry.set(pluginId, mod)
    // Auto-register any component exported as `Panel_*`
    for (const [key, component] of Object.entries(mod)) {
      if (key.startsWith('Panel_')) {
        const type = key.replace('Panel_', '').replace(/([A-Z])/g, (_, c: string) => `-${c.toLowerCase()}`).slice(1)
        registerPanelComponent(`${pluginId}:${type}`, component)
      }
    }
  } catch (e) {
    console.error(`[PluginRegistry] Failed to load bundle for ${pluginId}:`, e)
  }
}

export function getBundleComponents(pluginId: string): Record<string, Component> {
  return bundleRegistry.get(pluginId) ?? {}
}
