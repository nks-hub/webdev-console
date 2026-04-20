<!--
  Generic version grid: shows installed versions for a serviceId,
  lets users download new versions and switch the active one.
  Emits 'switch' with the version string after validation passes.
-->
<template>
  <div class="version-switcher">
    <div class="version-grid">
      <div
        v-for="v in versions"
        :key="v.version"
        class="version-tile"
        :class="{ active: v.version === activeVersion, installing: installing === v.version }"
        @click="requestSwitch(v.version)"
      >
        <span class="ver-label">{{ v.version }}</span>
        <el-tag v-if="v.version === activeVersion" type="success" size="small" effect="dark">Active</el-tag>
        <el-tag v-if="v.version === defaultVersion" type="info" size="small" effect="plain">Default</el-tag>
      </div>
    </div>

    <ValidationBadge ref="badge" @confirmed="applySwitch" @revert="cancelSwitch" />

    <div v-if="error" class="switch-error">{{ error }}</div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import ValidationBadge from './ValidationBadge.vue'
import { daemonBaseUrl } from '../../api/daemon'

interface VersionEntry {
  version: string
  installed: boolean
}

const props = defineProps<{
  serviceId: string
  versions: VersionEntry[]
  activeVersion?: string
  defaultVersion?: string
}>()

const emit = defineEmits<{
  switch: [version: string]
}>()

const badge = ref<InstanceType<typeof ValidationBadge> | null>(null)
const installing = ref<string | null>(null)
const pendingVersion = ref<string | null>(null)
const error = ref<string | null>(null)

async function requestSwitch(version: string) {
  if (version === props.activeVersion) return
  pendingVersion.value = version
  error.value = null
  badge.value?.startValidation()

  try {
    // Previously hard-coded a stale 50051 fallback (daemon default is
    // 5199); swapped to the shared resolver so browser dev mode hits
    // the correct URL even when the preload port file isn't readable.
    const res = await fetch(`${daemonBaseUrl()}/api/services/${props.serviceId}/validate-version`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ version }),
    })
    const data = await res.json() as { valid: boolean; error?: string }
    badge.value?.setResult(data.valid, data.error)
    if (!data.valid) error.value = data.error ?? 'Validation failed'
  } catch (e) {
    badge.value?.setResult(false, String(e))
    error.value = String(e)
  }
}

function applySwitch() {
  if (pendingVersion.value) emit('switch', pendingVersion.value)
  pendingVersion.value = null
}

function cancelSwitch() {
  pendingVersion.value = null
  badge.value?.reset()
}
</script>

<style scoped>
.version-switcher { display: flex; flex-direction: column; gap: 12px; }
.version-grid { display: flex; flex-wrap: wrap; gap: 8px; }
.version-tile {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 14px;
  border-radius: 8px;
  border: 1px solid var(--el-border-color);
  cursor: pointer;
  transition: border-color 0.2s, background 0.2s;
}
.version-tile:hover { border-color: var(--el-color-primary); background: var(--el-color-primary-light-9); }
.version-tile.active { border-color: var(--el-color-success); background: var(--el-color-success-light-9); }
.ver-label { font-weight: 600; font-size: 0.9rem; }
.switch-error { color: var(--el-color-danger); font-size: 0.82rem; }
</style>
