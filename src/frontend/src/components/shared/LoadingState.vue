<template>
  <div class="loading-state" :style="{ padding }">
    <el-icon class="is-loading"><Loading /></el-icon>
    <span>{{ label }}</span>
    <span v-if="showElapsed && elapsed > 4" class="elapsed mono">{{ elapsed }}s</span>
  </div>
</template>

<!--
  Shared loading indicator — replaces ad-hoc `<el-skeleton :rows="N">` on
  pages where the eventual row count is unknown (fake skeleton rows
  mislead). Icon + label + optional elapsed counter.
-->

<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount } from 'vue'
import { Loading } from '@element-plus/icons-vue'

withDefaults(
  defineProps<{ label?: string; padding?: string; showElapsed?: boolean }>(),
  { label: 'Loading…', padding: '48px 24px', showElapsed: true }
)

const elapsed = ref(0)
let timer: ReturnType<typeof setInterval> | null = null
const start = Date.now()
onMounted(() => {
  timer = setInterval(() => {
    elapsed.value = Math.floor((Date.now() - start) / 1000)
  }, 1000)
})
onBeforeUnmount(() => {
  if (timer) clearInterval(timer)
})
</script>

<style scoped>
.loading-state {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  color: var(--el-text-color-secondary);
  font-size: 14px;
  text-align: center;
}
.loading-state .el-icon {
  font-size: 16px;
}
.elapsed {
  opacity: 0.6;
  font-size: 12px;
  margin-left: 4px;
}
.mono {
  font-family: 'JetBrains Mono', monospace;
}
</style>
