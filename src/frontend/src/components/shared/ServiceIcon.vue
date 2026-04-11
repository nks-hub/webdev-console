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

const STATIC_ALIASES: Record<string, string> = {
  mariadb: 'mysql',
  mail: 'mailpit',
}

function staticUrlFor(id: string): string | null {
  const normalized = STATIC_ALIASES[id] || id
  const key = Object.keys(staticIcons).find(k => k.endsWith(`/brand/${normalized}.svg`))
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
  'nks.wdc.caddy': 'caddy',
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
  const knownPlugin = ['apache', 'caddy', 'php', 'mysql', 'redis', 'mailpit'].includes(shortId)
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
  width: 30px;
  height: 30px;
  flex-shrink: 0;
  border-radius: 10px;
  background:
    radial-gradient(circle at 30% 20%, rgba(255, 255, 255, 0.16), transparent 48%),
    linear-gradient(180deg, rgba(255, 255, 255, 0.07), rgba(255, 255, 255, 0.02));
  border: 1px solid rgba(255, 255, 255, 0.08);
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.06);
  color: var(--wdc-text-3);
  transition: opacity 0.12s, filter 0.12s, transform 0.12s, border-color 0.12s;
  user-select: none;
}
.svc-icon img {
  width: 78%;
  height: 78%;
  display: block;
  object-fit: contain;
  pointer-events: none;
}
.svc-icon.inactive {
  opacity: 0.5;
  filter: saturate(0.2) brightness(0.98);
}
.svc-icon:not(.inactive) {
  border-color: rgba(255, 255, 255, 0.12);
  transform: translateY(-0.5px);
}
.fallback {
  font-size: 0.8rem;
  font-weight: 700;
  color: var(--wdc-text);
}
</style>
