<template>
  <section class="settings-card">
    <header class="settings-card-header">
      <span class="settings-card-title">{{ title }}</span>
      <slot name="meta" />
    </header>
    <div class="settings-card-body">
      <slot />
    </div>
  </section>
</template>

<script setup lang="ts">
defineProps<{
  title: string
}>()
</script>

<style scoped>
.settings-card {
  border: 1px solid var(--wdc-border);
  border-radius: 8px;
  background: var(--wdc-surface-2);
  margin-bottom: 12px;
}

.settings-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  border-bottom: 1px solid var(--wdc-border);
}

.settings-card-title {
  font-weight: 600;
  color: var(--wdc-text-1);
}

.settings-card-body {
  padding: 18px;
}
</style>
