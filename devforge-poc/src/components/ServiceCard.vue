<template>
  <el-card class="service-card" shadow="always">
    <template #header>
      <div class="card-header">
        <span class="card-title">Notepad Service</span>
        <el-tag :type="tagType" size="small" effect="dark">{{ tagLabel }}</el-tag>
      </div>
    </template>

    <div class="card-body">
      <div class="stat-row">
        <span class="label">Daemon</span>
        <el-tag :type="store.connected ? 'success' : 'danger'" size="small" effect="plain">
          {{ store.connected ? 'Connected' : 'Disconnected' }}
        </el-tag>
      </div>
      <div class="stat-row" v-if="store.status">
        <span class="label">Version</span>
        <span class="value">{{ store.status.version }}</span>
      </div>
      <div class="stat-row" v-if="store.status">
        <span class="label">Uptime</span>
        <span class="value">{{ store.status.uptime }}s</span>
      </div>
      <div class="stat-row" v-if="store.notepad?.pid">
        <span class="label">PID</span>
        <span class="value">{{ store.notepad.pid }}</span>
      </div>
    </div>

    <div class="card-actions">
      <el-button
        type="success"
        :disabled="isRunning || store.loading || !store.connected"
        :loading="store.loading"
        @click="store.start()"
      >Start</el-button>
      <el-button
        type="danger"
        :disabled="!isRunning || store.loading || !store.connected"
        :loading="store.loading"
        @click="store.stop()"
      >Stop</el-button>
    </div>
  </el-card>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useDaemonStore } from '../stores/daemon'

const store = useDaemonStore()

const isRunning = computed(() => store.notepad?.status === 'running')
const tagType = computed(() => isRunning.value ? 'success' : 'info')
const tagLabel = computed(() => isRunning.value ? 'Running' : 'Stopped')
</script>

<style scoped>
.service-card { width: 360px; }
.card-header { display: flex; align-items: center; justify-content: space-between; }
.card-title { font-size: 1rem; font-weight: 600; }
.card-body { display: flex; flex-direction: column; gap: 10px; margin-bottom: 16px; }
.stat-row { display: flex; align-items: center; justify-content: space-between; }
.label { color: var(--el-text-color-secondary); font-size: 0.85rem; }
.value { font-size: 0.85rem; font-family: monospace; }
.card-actions { display: flex; gap: 8px; }
</style>
