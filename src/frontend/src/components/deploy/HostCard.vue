<template>
  <el-card class="host-card" shadow="hover">
    <template #header>
      <div class="host-card-head">
        <div class="host-card-title">
          <el-checkbox
            v-if="selectable"
            :model-value="selected"
            :aria-label="`Include ${host} in group deploy`"
            @update:model-value="(v: any) => $emit('toggle-select', !!v)"
            @click.stop
          />
          <span class="host-card-name">{{ host }}</span>
          <el-tag v-if="isProduction" type="danger" size="small" effect="plain">PROD</el-tag>
        </div>
        <HealthBadge :state="healthState" />
      </div>
    </template>

    <div class="host-card-body">
      <div v-if="lastDeploy" class="host-card-row">
        <span class="muted">Last deploy:</span>
        <span class="mono">{{ formatRelative(lastDeploy.startedAt) }}</span>
        <el-tag :type="lastDeploy.success ? 'success' : 'danger'" size="small" effect="plain">
          {{ lastDeploy.success ? 'OK' : 'FAILED' }}
        </el-tag>
      </div>
      <div v-else class="muted">No deploys yet</div>

      <div v-if="lastDeploy?.releaseId" class="host-card-row">
        <span class="muted">Release:</span>
        <span class="mono">{{ lastDeploy.releaseId }}</span>
      </div>
    </div>

    <template #footer>
      <div class="host-card-footer">
        <el-button type="primary" @click="$emit('deploy')">Deploy</el-button>
        <el-button
          v-if="lastDeploy && lastDeploy.success"
          plain
          @click="$emit('rollback')"
        >Rollback</el-button>
      </div>
    </template>
  </el-card>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import HealthBadge from './HealthBadge.vue'
import type { DeployHistoryEntryDto } from '../../api/deploy'

const props = defineProps<{
  host: string
  lastDeploy?: DeployHistoryEntryDto | null
  /** Phase 6.10 — show a checkbox for multi-select group deploy. */
  selectable?: boolean
  /** Phase 6.10 — current selection state (controlled by parent). */
  selected?: boolean
}>()

defineEmits<{
  deploy: []
  rollback: []
  'toggle-select': [value: boolean]
}>()

const isProduction = computed(() =>
  props.host.toLowerCase().includes('prod') || props.host.toLowerCase() === 'production')

const healthState = computed<'healthy' | 'degraded' | 'down' | 'unknown'>(() => {
  if (!props.lastDeploy) return 'unknown'
  if (props.lastDeploy.error) return 'down'
  return 'healthy'
})

function formatRelative(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime()
  if (ms < 60_000) return `${Math.floor(ms / 1000)}s ago`
  if (ms < 3_600_000) return `${Math.floor(ms / 60_000)}m ago`
  if (ms < 86_400_000) return `${Math.floor(ms / 3_600_000)}h ago`
  return `${Math.floor(ms / 86_400_000)}d ago`
}
</script>

<style scoped>
.host-card { width: 100%; }
.host-card-head {
  display: flex; align-items: center; justify-content: space-between; gap: 12px;
}
.host-card-title { display: flex; align-items: center; gap: 8px; }
.host-card-name { font-weight: 600; font-size: 14px; }

.host-card-body {
  display: flex; flex-direction: column; gap: 8px;
  font-size: 13px;
}
.host-card-row { display: flex; align-items: center; gap: 8px; }
.muted { color: var(--el-text-color-secondary); }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }

.host-card-footer { display: flex; gap: 8px; justify-content: flex-end; }
</style>
