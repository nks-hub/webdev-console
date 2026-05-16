<template>
  <!-- F91.6: iterate plugin contributions targeted at `name` and render
       each via the frontend component registry. Props are the union of
       plugin-declared props + caller-supplied context. Unknown component
       types are skipped (never thrown) so a misbehaving plugin can't
       blow up the shell. -->
  <template v-for="c in contributions" :key="c.pluginId + ':' + c.componentType + ':' + c.order">
    <component
      v-if="resolveComponent(c.componentType)"
      :is="resolveComponent(c.componentType)"
      v-bind="mergedAttrs(c)"
      :plugin-id="c.pluginId"
    />
  </template>
</template>

<script setup lang="ts">
import { computed, defineAsyncComponent, markRaw, useAttrs, type Component } from 'vue'
import { usePluginsStore } from '../../stores/plugins'
import { resolvePluginComponent } from '../../plugin-components/registry'

// inheritAttrs:false because this component renders a v-for of children;
// without this Vue tries to apply parent-bound listeners to the wrapping
// fragment which silently drops them. We forward $attrs (incl. on*
// listener entries like onUpdate:site, onDirty) onto each child component
// explicitly via mergedAttrs() below. Pre-fix incident 2026-05-07: the
// CloudflareSiteTab toggle emitted update:site + dirty, but SiteEdit
// never received them, so Save&Apply saw a clean form and tunnel
// enablement silently no-op'd.
defineOptions({ inheritAttrs: false })

const props = defineProps<{
  /** Slot name — plugins contribute to this via schema.Contribute(slot, …). */
  name: string
  /** Page-supplied reactive context merged into each contribution's props. */
  context?: Record<string, unknown>
}>()

const fwdAttrs = useAttrs()

const pluginsStore = usePluginsStore()

const contributions = computed(() => pluginsStore.contributionsForSlot(props.name))

// Cache resolved async components so each re-render doesn't create a fresh
// async wrapper (which would retrigger suspense + show loading states).
const resolved: Record<string, Component> = {}
function resolveComponent(type: string): Component | undefined {
  if (resolved[type]) return resolved[type]
  const loader = resolvePluginComponent(type)
  if (!loader) return undefined
  const comp = markRaw(defineAsyncComponent(loader))
  resolved[type] = comp
  return comp
}

function mergeProps(
  declared: Record<string, unknown>,
  context?: Record<string, unknown>,
): Record<string, unknown> {
  return context ? { ...declared, ...context } : declared
}

interface Contribution {
  componentType: string
  pluginId: string
  props: Record<string, unknown>
  order: number
}

// Combine declared plugin props + page context + the page's listeners
// (forwarded from $attrs). Order matters: later spreads win, so listener
// overrides are last.
function mergedAttrs(c: Contribution): Record<string, unknown> {
  return { ...mergeProps(c.props, props.context), ...fwdAttrs }
}
</script>
