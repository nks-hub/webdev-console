<template>
  <span
    class="health-dot"
    :class="['health-dot-' + level, pulse ? 'health-dot-pulse' : null]"
    :title="title || defaultLabel"
    :aria-label="title || defaultLabel"
    role="img"
  />
</template>

<script setup lang="ts">
/**
 * Plan §6 shared primitive — single status indicator dot used across
 * Dashboard, Settings, plugin pages, and detail rows. Replaces ad-hoc
 * inline `status-dot ok/err` spans so the visual language stays
 * consistent and one change here propagates everywhere.
 *
 * Levels:
 *   ok      — service running, check passed
 *   warn    — degraded but functional
 *   err     — failed, stopped, blocked
 *   muted   — unknown / not applicable / disabled
 */
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'

defineOptions({ name: 'HealthStatusDot' })
const props = defineProps<{
  level: 'ok' | 'warn' | 'err' | 'muted'
  pulse?: boolean
  title?: string
}>()

const { t } = useI18n()
// Spoken/tooltip fallback when the caller passes no title — the dot's
// colour alone carries no meaning for screen readers or colour-blind users.
const defaultLabel = computed(() => t(`common.status.${props.level}`))
</script>

<style scoped>
.health-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  vertical-align: middle;
  flex-shrink: 0;
  background: var(--el-text-color-disabled);
}
.health-dot-ok { background: var(--wdc-status-running, var(--el-color-success)); }
.health-dot-warn { background: var(--el-color-warning); }
.health-dot-err { background: var(--wdc-status-error, var(--el-color-danger)); }
.health-dot-muted { background: var(--el-text-color-disabled); }

.health-dot-pulse {
  animation: wdc-dot-pulse 1.8s ease-in-out infinite;
}
@keyframes wdc-dot-pulse {
  0%, 100% { opacity: 1; transform: scale(1); }
  50%      { opacity: 0.55; transform: scale(1.25); }
}
</style>
