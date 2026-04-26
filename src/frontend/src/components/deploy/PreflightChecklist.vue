<template>
  <div class="preflight">
    <h4 class="preflight-title">
      Preflight
      <el-button v-if="!loading" link size="small" @click="$emit('rerun')">
        <el-icon><Refresh /></el-icon> Re-run
      </el-button>
    </h4>
    <ul class="preflight-list" role="list">
      <li
        v-for="check in checks"
        :key="check.name"
        :class="['preflight-row', `preflight-row--${check.state}`]"
        role="listitem"
      >
        <span class="preflight-icon" :aria-label="`${check.name}: ${check.state}`">
          <el-icon v-if="check.state === 'running'" class="is-loading"><Loading /></el-icon>
          <el-icon v-else-if="check.state === 'pass'"><CircleCheck /></el-icon>
          <el-icon v-else-if="check.state === 'fail'"><CircleClose /></el-icon>
          <el-icon v-else-if="check.state === 'warn'"><WarningFilled /></el-icon>
          <span v-else class="preflight-pending-dot" />
        </span>
        <span class="preflight-name mono">{{ check.name }}</span>
        <span class="preflight-msg">{{ check.message }}</span>
      </li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { CircleCheck, CircleClose, Loading, Refresh, WarningFilled } from '@element-plus/icons-vue'

export interface PreflightCheck {
  name: string
  state: 'pending' | 'running' | 'pass' | 'warn' | 'fail'
  message: string
}

defineProps<{
  checks: PreflightCheck[]
  loading?: boolean
}>()

defineEmits<{ rerun: [] }>()
</script>

<style scoped>
.preflight { display: flex; flex-direction: column; gap: 8px; }
.preflight-title {
  display: flex; align-items: center; justify-content: space-between;
  margin: 0; font-size: 13px; color: var(--el-text-color-regular);
}
.preflight-list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
.preflight-row {
  display: grid; grid-template-columns: 24px auto 1fr; gap: 10px;
  align-items: center; padding: 5px 8px; border-left: 3px solid transparent;
  font-size: 13px;
}
.preflight-row--pending { color: var(--el-text-color-secondary); border-left-color: var(--el-border-color); }
.preflight-row--running { border-left-color: var(--el-color-primary); background: var(--el-color-primary-light-9); }
.preflight-row--pass { border-left-color: var(--el-color-success); }
.preflight-row--warn { border-left-color: var(--el-color-warning); background: var(--el-color-warning-light-9); }
.preflight-row--fail { border-left-color: var(--el-color-danger); background: var(--el-color-danger-light-9); color: var(--el-color-danger); }
.preflight-pending-dot { display: inline-block; width: 7px; height: 7px; border-radius: 50%; background: var(--el-border-color); }
.preflight-name { font-weight: 600; }
.preflight-msg { color: var(--el-text-color-secondary); font-size: 12px; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
</style>
