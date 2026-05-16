<template>
  <section class="settings-panel" :class="panelClass">
    <header v-if="title || $slots.header" class="settings-panel-header">
      <span v-if="$slots.icon || icon" class="settings-panel-icon">
        <slot name="icon">
          <component :is="icon" v-if="icon" />
        </slot>
      </span>
      <div class="settings-panel-titles">
        <slot name="header">
          <h2 class="settings-panel-title">{{ title }}</h2>
          <p v-if="subtitle" class="settings-panel-subtitle">{{ subtitle }}</p>
        </slot>
      </div>
    </header>
    <div class="settings-panel-body">
      <slot />
    </div>
    <footer v-if="$slots.actions" class="settings-panel-actions">
      <slot name="actions" />
    </footer>
  </section>
</template>

<script setup lang="ts">
/**
 * Plan §6 shared primitive — Settings panel card. Wraps the recurring
 * "icon + h2 title + subtitle + body + footer actions" shape used by
 * the simple-settings-grid and advanced settings cards.
 *
 * Slot conventions:
 *   #icon       — optional Element Plus <el-icon> wrapper (alternative
 *                 to passing `icon` prop directly)
 *   #header     — full custom header replacing title/subtitle
 *   default     — main panel body
 *   #actions    — footer button row
 */
import type { Component } from 'vue'

defineOptions({ name: 'SettingsPanel' })
defineProps<{
  title?: string
  subtitle?: string
  icon?: Component
  panelClass?: string
}>()
</script>

<style scoped>
.settings-panel {
  min-width: 0;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 18px;
}

.settings-panel-header {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: 14px;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--wdc-border);
}

.settings-panel-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  background: color-mix(in oklab, var(--wdc-accent) 14%, transparent);
  color: var(--wdc-accent);
  flex-shrink: 0;
}

.settings-panel-icon :deep(.el-icon) {
  font-size: 18px;
}

.settings-panel-titles {
  min-width: 0;
  flex: 1;
}

.settings-panel-title {
  margin: 0;
  color: var(--wdc-text);
  font-size: 1rem;
  font-weight: 800;
}

.settings-panel-subtitle {
  margin: 3px 0 0;
  color: var(--wdc-text-2);
  font-size: 0.82rem;
  line-height: 1.4;
}

.settings-panel-body {
  min-width: 0;
}

.settings-panel-actions {
  display: flex;
  gap: 8px;
  margin-top: 10px;
  flex-wrap: wrap;
}
</style>
