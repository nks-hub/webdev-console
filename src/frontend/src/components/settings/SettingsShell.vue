<template>
  <div class="settings-shell">
    <nav class="shell-nav" :aria-label="ariaLabel">
      <div v-for="group in groups" :key="group.name" class="shell-group">
        <div class="shell-group-label">{{ group.label }}</div>
        <div role="tablist" aria-orientation="vertical" class="shell-tablist">
          <button
            v-for="section in group.sections"
            :key="section.name"
            :ref="(el) => setTabRef(section.name, el)"
            type="button"
            role="tab"
            class="shell-tab"
            :class="{ active: section.name === modelValue }"
            :aria-selected="section.name === modelValue"
            :tabindex="section.name === modelValue ? 0 : -1"
            @click="select(section.name)"
            @keydown="onKey($event, section.name)"
          >
            {{ section.label }}
          </button>
        </div>
      </div>
    </nav>

    <div class="shell-body" role="tabpanel">
      <slot />
      <div v-if="$slots.footer" class="shell-footer">
        <slot name="footer" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, type ComponentPublicInstance } from 'vue'

/**
 * Shared settings layout: grouped vertical section list + content area +
 * optional sticky footer. Replaces the nine-tab horizontal strip on the
 * Settings page, which wrapped onto two lines and hid the grouping (app
 * vs. environment vs. integration). The content stays with the caller —
 * render each section with v-show="active === name".
 *
 * Keyboard: arrows move between sections (roving tabindex), Home/End jump.
 */
export interface SettingsSection {
  name: string
  label: string
  group: string
}

const props = defineProps<{
  modelValue: string
  sections: SettingsSection[]
  groupLabels?: Record<string, string>
  ariaLabel?: string
}>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

const groups = computed(() => {
  const order: string[] = []
  const byGroup = new Map<string, SettingsSection[]>()
  for (const s of props.sections) {
    if (!byGroup.has(s.group)) { byGroup.set(s.group, []); order.push(s.group) }
    byGroup.get(s.group)!.push(s)
  }
  return order.map(name => ({ name, label: props.groupLabels?.[name] ?? name, sections: byGroup.get(name)! }))
})

const tabRefs = new Map<string, HTMLElement>()
function setTabRef(name: string, el: Element | ComponentPublicInstance | null) {
  if (el instanceof HTMLElement) tabRefs.set(name, el)
  else tabRefs.delete(name)
}

function select(name: string) {
  if (name !== props.modelValue) emit('update:modelValue', name)
}

function onKey(e: KeyboardEvent, current: string) {
  const names = props.sections.map(s => s.name)
  const i = names.indexOf(current)
  let next: string | undefined
  if (e.key === 'ArrowDown' || e.key === 'ArrowRight') next = names[(i + 1) % names.length]
  else if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') next = names[(i - 1 + names.length) % names.length]
  else if (e.key === 'Home') next = names[0]
  else if (e.key === 'End') next = names[names.length - 1]
  if (!next) return
  e.preventDefault()
  select(next)
  void nextTick(() => tabRefs.get(next!)?.focus())
}
</script>

<style scoped>
.settings-shell {
  display: grid;
  grid-template-columns: 200px minmax(0, 1fr);
  gap: var(--wdc-space-5);
  align-items: start;
}
.shell-nav {
  position: sticky;
  top: var(--wdc-space-4);
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4);
}
.shell-group { display: flex; flex-direction: column; gap: var(--wdc-space-1); }
.shell-group-label {
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--wdc-text-3);
  padding: 0 var(--wdc-space-3);
}
.shell-tablist { display: flex; flex-direction: column; gap: 2px; }
.shell-tab {
  appearance: none;
  border: 0;
  background: transparent;
  color: var(--wdc-text-2);
  font: inherit;
  text-align: left;
  padding: var(--wdc-space-2) var(--wdc-space-3);
  border-radius: var(--wdc-radius-sm);
  cursor: pointer;
}
.shell-tab:hover { background: var(--wdc-surface-2); color: var(--wdc-text); }
.shell-tab.active {
  background: var(--wdc-accent-dim);
  color: var(--wdc-text);
  font-weight: 600;
}
.shell-tab:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: -2px; }
.shell-body { min-width: 0; display: flex; flex-direction: column; gap: var(--wdc-space-4); }
.shell-footer {
  position: sticky;
  bottom: 0;
  display: flex;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-3) 0;
  background: var(--wdc-bg);
  border-top: 1px solid var(--wdc-border);
}

/* Narrow windows: the section list becomes a horizontal strip. */
@media (max-width: 760px) {
  .settings-shell { grid-template-columns: minmax(0, 1fr); }
  .shell-nav {
    position: static;
    flex-direction: row;
    overflow-x: auto;
    gap: var(--wdc-space-3);
  }
  .shell-tablist { flex-direction: row; }
  .shell-group-label { display: none; }
  .shell-tab { white-space: nowrap; }
}
</style>
