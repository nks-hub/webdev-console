<!--
  SchemaRenderer: renders a plugin's UI from its JSON panel definition (Approach A + C).
  For each panel definition, resolves a Vue component by type and passes props.
  If panel.type === 'custom', looks for a bundle-registered component instead.
-->
<template>
  <div class="schema-renderer">
    <template v-for="(panel, idx) in definition.panels" :key="idx">
      <component
        v-if="resolvedComponents[idx]"
        :is="resolvedComponents[idx]"
        v-bind="panel.props"
        class="panel-component"
      />
      <el-alert
        v-else
        type="warning"
        :closable="false"
        :title="`Unknown panel type: ${panel.type}`"
        class="panel-component"
      />
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import type { Component } from 'vue'
import type { PluginUiDefinition } from '../api/types'
import { resolvePanelComponent, loadPluginBundle } from './PluginRegistry'

const props = defineProps<{
  pluginId: string
  definition: PluginUiDefinition
}>()

// Eagerly load the bundle if this plugin has one (Approach B)
onMounted(async () => {
  if (props.definition.bundleUrl) {
    await loadPluginBundle(props.pluginId, props.definition.bundleUrl)
  }
})

const resolvedComponents = computed<Array<Component | null>>(() =>
  props.definition.panels.map(p => {
    // Namespaced lookup for custom components registered by a bundle
    const namespaced = resolvePanelComponent(`${props.pluginId}:${p.type}`)
    if (namespaced) return namespaced
    return resolvePanelComponent(p.type)
  })
)
</script>

<style scoped>
.schema-renderer { display: flex; flex-direction: column; gap: 16px; }
.panel-component { width: 100%; }
</style>
