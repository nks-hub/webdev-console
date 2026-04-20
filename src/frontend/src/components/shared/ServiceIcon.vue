<template>
  <span class="svc-icon" :class="{ inactive: !active }" :data-service="service">
    <img v-if="iconUrl" :src="iconUrl" :alt="service" draggable="false" @error="onError" />
    <span v-else class="fallback">{{ fallbackLetter }}</span>
  </span>
</template>

<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { daemonBaseUrl, daemonToken } from '../../api/daemon'

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

const loadError = ref(false)

// Map daemon plugin ids to short service ids — handles "nks.wdc.apache" from
// /api/plugins manifests AND "apache" from /api/services statuses.
const SHORT_ID: Record<string, string> = {
  'nks.wdc.apache': 'apache',
  'nks.wdc.caddy': 'caddy',
  'nks.wdc.php': 'php',
  'nks.wdc.mysql': 'mysql',
  'nks.wdc.redis': 'redis',
  'nks.wdc.mailpit': 'mailpit',
}

// Normalise any input (long plugin id, short service id) to a known short id or
// null. Used for BOTH the daemon endpoint path and the static fallback lookup.
function normalise(id: string): string {
  return SHORT_ID[id] || id
}

const iconUrl = computed(() => {
  const shortId = normalise(props.service)
  // Static fallback path (when daemon img 404/401 happened): look up static
  // AFTER normalising so "nks.wdc.apache" → "apache.svg" instead of
  // "nks.wdc.apache.svg" which doesn't exist and triggers the "N" letter.
  if (loadError.value) return staticUrlFor(shortId)
  // Prefer daemon endpoint so users can swap plugin DLLs without rebuilding
  // frontend. The daemon requires auth on /api/*; <img> tags can't set
  // headers, so pipe the token through the query string (the auth middleware
  // in Program.cs accepts `?token=` as a fallback to the Bearer header).
  // Without this every brand icon 401s and the sidebar fills up with "N".
  const knownPlugin = ['apache', 'caddy', 'php', 'mysql', 'redis', 'mailpit'].includes(shortId)
  if (knownPlugin) {
    const token = daemonToken()
    const suffix = token ? `?token=${encodeURIComponent(token)}` : ''
    return `${daemonBaseUrl()}/api/plugins/${shortId}/icon${suffix}`
  }
  return staticUrlFor(shortId)
})

function onError() {
  loadError.value = true
}

// Pick a meaningful fallback letter when neither daemon nor static icon exists.
// Plugin ids like "nks.wdc.hosts" have "N" as first char which is useless for
// identification. Use the LAST segment after the final dot ("hosts" → "H"),
// falling through to the raw service id if there are no dots.
const fallbackLetter = computed(() => {
  const s = props.service || ''
  if (!s) return '?'
  const lastSegment = s.includes('.') ? s.substring(s.lastIndexOf('.') + 1) : s
  return (lastSegment || s).charAt(0).toUpperCase()
})

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
  border-radius: var(--wdc-radius-sm);
  /* Flat: solid surface-2 tile, single border — no gradients, no inset shadow */
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  color: var(--wdc-text-3);
  transition: border-color 0.12s, background 0.12s;
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
