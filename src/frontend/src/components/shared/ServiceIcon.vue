<template>
  <span class="svc-icon" :class="{ inactive: !active }" :data-service="service">
    <img v-if="iconUrl" :src="iconUrl" :alt="service" draggable="false" @error="onError" />
    <span v-else class="fallback">{{ (service || '?').charAt(0).toUpperCase() }}</span>
  </span>
</template>

<script setup lang="ts">
import { ref, watch, computed } from 'vue'

const props = defineProps<{
  service: string
  active: boolean
}>()

// Static frontend fallback assets — imported so Vite bundles them
const staticIcons = import.meta.glob<string>('../../assets/brand/*.svg', { eager: true, query: '?url', import: 'default' })

function staticUrlFor(id: string): string | null {
  const key = Object.keys(staticIcons).find(k => k.endsWith(`/brand/${id}.svg`))
  return key ? (staticIcons[key] as unknown as string) : null
}

function daemonBase(): string {
  const params = new URLSearchParams(window.location.search)
  const port = params.get('port') || (window as any).daemonApi?.getPort?.() || '5199'
  return `http://localhost:${port}`
}

const loadError = ref(false)

// Map daemon plugin ids to short service ids
const SHORT_ID: Record<string, string> = {
  'nks.wdc.apache': 'apache',
  'nks.wdc.php': 'php',
  'nks.wdc.mysql': 'mysql',
  'nks.wdc.redis': 'redis',
  'nks.wdc.mailpit': 'mailpit',
}

const iconUrl = computed(() => {
  if (loadError.value) return staticUrlFor(props.service)
  // Prefer daemon endpoint so users can swap plugin DLLs without rebuilding frontend
  // Fall back to static asset if service id doesn't have known plugin
  const shortId = SHORT_ID[props.service] || props.service
  const knownPlugin = ['apache', 'php', 'mysql', 'redis', 'mailpit'].includes(shortId)
  if (knownPlugin) return `${daemonBase()}/api/plugins/${shortId}/icon`
  return staticUrlFor(shortId)
})

function onError() {
  loadError.value = true
}

watch(() => props.service, () => { loadError.value = false })
</script>

<style scoped>
.svc-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
  color: var(--wdc-text-3);
  transition: opacity 0.12s, filter 0.12s;
  user-select: none;
}
.svc-icon img {
  width: 100%;
  height: 100%;
  display: block;
  object-fit: contain;
  pointer-events: none;
}
.svc-icon.inactive {
  opacity: 0.45;
  filter: saturate(0.12) brightness(0.85);
}
.fallback {
  font-size: 0.72rem;
  font-weight: 700;
  color: var(--wdc-text-2);
}
</style>
