<template>
  <section class="edit-card">
    <header v-if="title || $slots.header || $slots.hint || hint" class="edit-card-header">
      <span class="edit-card-title">
        <slot name="title">{{ title }}</slot>
      </span>
      <span v-if="$slots.hint || hint" class="edit-card-hint">
        <slot name="hint">{{ hint }}</slot>
      </span>
    </header>
    <div class="edit-card-body" :class="{ 'edit-card-body-flush': flushBody }">
      <slot />
    </div>
  </section>
</template>

<script setup lang="ts">
/**
 * Plan §6 shared primitive — edit-card. The 196-instance pattern of
 * `<section class="edit-card"><header class="edit-card-header">...
 * <div class="edit-card-body">...</div></section>` used by plugin
 * pages (Apache/MySQL/Postgres/PHP/Redis/Mailpit) and advanced
 * Settings tabs.
 *
 * Slots:
 *   #title  — overrides the title prop (rich content)
 *   #hint   — overrides the hint prop (e.g. a small el-button)
 *   default — body content
 *
 * Props:
 *   title       — uppercase section title text
 *   hint        — right-aligned secondary text
 *   flushBody   — drop the 18px body padding (for log viewers, tables)
 */
defineOptions({ name: 'EditCard' })
defineProps<{
  title?: string
  hint?: string
  flushBody?: boolean
}>()
</script>

<style scoped>
.edit-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  overflow: hidden;
}
.edit-card-header {
  padding: 14px 20px;
  background: var(--wdc-surface-2);
  border-bottom: 1px solid var(--wdc-border);
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 12px;
}
.edit-card-title {
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text);
}
.edit-card-hint {
  font-size: 0.75rem;
  color: var(--wdc-text-3);
}
.edit-card-body {
  padding: 18px 20px;
}
.edit-card-body-flush {
  padding: 0;
}
</style>
