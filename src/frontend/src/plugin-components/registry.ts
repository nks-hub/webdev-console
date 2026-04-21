// F91.6: frontend component registry for plugin-contributed UI.
//
// Plugins (C# DLLs) can't ship Vue code directly, so they reference
// components by string type name via UiContribution.ComponentType. This
// file is the single source of truth mapping those type names to
// concrete Vue components. Adding a new pluggable surface is a two-step:
//   1. New plugin calls schema.Contribute("some-slot", "my-panel", {...}).
//   2. Register 'my-panel' here pointing at a .vue file.
//
// Dynamic import is intentional — each panel lazy-loads only if a plugin
// actually uses it, so disabled plugins don't inflate the main bundle.

import type { Component } from 'vue'

type LazyComponent = () => Promise<Component | { default: Component }>

export const PLUGIN_COMPONENTS: Record<string, LazyComponent> = {
  // Generic primitives (usable by any plugin):
  'nav-link-button': () => import('./NavLinkButton.vue'),
  'stat-tile': () => import('./StatTile.vue'),

  // SSL plugin:
  'ssl-site-tab': () => import('./SslSiteTab.vue'),

  // Cloudflare plugin:
  'cloudflare-site-tab': () => import('./CloudflareSiteTab.vue'),
  'cloudflare-tunnel-badge': () => import('./CloudflareTunnelBadge.vue'),

  // Composer plugin:
  'composer-site-tab': () => import('./ComposerSiteTab.vue'),
}

/**
 * Resolve a componentType to its loader. Returns undefined (so <PluginSlot>
 * can skip rendering) when the string is unknown — a plugin shipping a
 * bogus type name never crashes the shell.
 */
export function resolvePluginComponent(componentType: string): LazyComponent | undefined {
  return PLUGIN_COMPONENTS[componentType]
}
