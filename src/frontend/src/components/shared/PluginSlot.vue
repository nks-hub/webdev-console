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
      v-bind="mergeProps(c.props, context)"
      :plugin-id="c.pluginId"
    />
  </template>
</template>

<script setup lang="ts">
import { computed, defineAsyncComponent, markRaw, type Component } from 'vue'
import { usePluginsStore } from '../../stores/plugins'
import { resolvePluginComponent } from '../../plugin-components/registry'

const props = defineProps<{
  /** Slot name — plugins contribute to this via schema.Contribute(slot, …). */
  name: string
  /** Page-supplied reactive context merged into each contribution's props. */
  context?: Record<string, unknown>
}>()

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
</script>
