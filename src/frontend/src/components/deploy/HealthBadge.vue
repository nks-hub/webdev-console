<template>
  <span
    :class="['hb', `hb--${stateClass}`, { 'hb--pulsing': pulsing }]"
    role="img"
    :aria-label="ariaLabel"
  >
    <el-icon><component :is="icon" /></el-icon>
    <span class="hb-label">{{ label }}</span>
  </span>
</template>

<script setup lang="ts">
/**
 * Health-state badge used on HostCard and the DeployRunDrawer header.
 * Per v2 a11y audit: pulse fires ONLY on state change, not continuously
 * (avoids vestibular triggers + banner-blindness). Reduced-motion media
 * query disables pulse entirely. Color is NEVER the only signal — text
 * label and icon glyph also encode state.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { CircleCheck, CircleClose, Loading, QuestionFilled } from '@element-plus/icons-vue'

type HealthState = 'healthy' | 'degraded' | 'down' | 'unknown'

const props = withDefaults(defineProps<{
  state: HealthState
  label?: string
}>(), {
  label: '',
})

const stateClass = computed(() => props.state)

const icon = computed(() => {
  switch (props.state) {
    case 'healthy': return CircleCheck
    case 'degraded': return Loading
    case 'down': return CircleClose
    case 'unknown':
    default: return QuestionFilled
  }
})

const label = computed(() => props.label || ({
  healthy: 'OK',
  degraded: 'Slow',
  down: 'Down',
  unknown: 'Unknown',
} as const)[props.state])

const ariaLabel = computed(() => `Health: ${label.value}`)

// Pulse controller — fires for ~600ms whenever `state` transitions, then
// settles to a static dot.
const pulsing = ref(false)
let pulseTimer: ReturnType<typeof setTimeout> | null = null

function triggerPulse() {
  if (pulseTimer) clearTimeout(pulseTimer)
  pulsing.value = true
  pulseTimer = setTimeout(() => { pulsing.value = false }, 600)
}

watch(() => props.state, (now, prev) => {
  if (now !== prev) triggerPulse()
})

onMounted(() => {
  // Don't pulse on initial mount — only on real transitions afterwards.
})
</script>

<style scoped>
.hb {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 8px;
  border-radius: 4px;
  font-size: 12px;
  font-weight: 600;
  border: 1px solid var(--el-border-color);
}
.hb--healthy { color: var(--el-color-success); border-color: var(--el-color-success-light-5); }
.hb--degraded { color: var(--el-color-warning); border-color: var(--el-color-warning-light-5); }
.hb--down { color: var(--el-color-danger); border-color: var(--el-color-danger-light-5); }
.hb--unknown { color: var(--el-text-color-secondary); }
.hb-label { letter-spacing: 0.02em; }

@keyframes hb-pulse {
  0%, 100% { box-shadow: 0 0 0 0 transparent; }
  50% { box-shadow: 0 0 0 6px currentColor; opacity: 0.8; }
}
.hb--pulsing { animation: hb-pulse 0.6s ease-out 1; }

@media (prefers-reduced-motion: reduce) {
  .hb--pulsing { animation: none; outline: 2px solid currentColor; outline-offset: 1px; }
}
</style>
