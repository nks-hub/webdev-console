<template>
  <div class="splash-overlay">
    <div class="splash-card">
      <div class="splash-logo">NKS</div>
      <div class="splash-title">WebDev Console</div>
      <div class="splash-progress-wrap">
        <div class="splash-progress-track">
          <div class="splash-progress-bar" :style="{ width: percent + '%' }" />
        </div>
        <span class="splash-pct">{{ percent }}%</span>
      </div>
      <div class="splash-stage">{{ stageLabel }}</div>
      <div class="splash-message">{{ message }}</div>
      <div v-if="error" class="splash-error">{{ error }}</div>
      <button v-if="error" class="splash-retry-btn" @click="retry">{{ t('splash.retry') }}</button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const stage = ref('starting')
const percent = ref(0)
const message = ref('')
const error = ref('')
let pollHandle: ReturnType<typeof setInterval> | null = null

const stageLabel = computed(() => {
  const map: Record<string, string> = {
    starting: t('splash.stages.starting'),
    database: t('splash.stages.database'),
    plugins: t('splash.stages.plugins'),
    services: t('splash.stages.services'),
    http: t('splash.stages.http'),
    ready: t('splash.stages.ready'),
  }
  return map[stage.value] ?? stage.value
})

async function pollProgress() {
  const port = window.daemonApi?.getPort?.()
  if (!port) return
  try {
    const r = await fetch(`http://localhost:${port}/api/boot/progress`)
    if (r.ok) {
      const data = await r.json()
      stage.value = data.stage ?? stage.value
      percent.value = data.percent ?? percent.value
      const events: { Message: string }[] = data.events ?? []
      if (events.length > 0) message.value = events[events.length - 1].Message
      error.value = data.lastError ?? ''
    }
  } catch { /* daemon not ready yet — keep waiting */ }
}

function retry() {
  error.value = ''
  percent.value = 0
  stage.value = 'starting'
  message.value = ''
  ;(window as any).electronAPI?.relaunchApp?.()
}

onMounted(() => {
  pollHandle = setInterval(pollProgress, 500)
})

onUnmounted(() => {
  if (pollHandle) { clearInterval(pollHandle); pollHandle = null }
})
</script>

<style scoped>
.splash-overlay {
  position: fixed;
  inset: 0;
  z-index: 10000;
  display: flex;
  align-items: center;
  justify-content: center;
  background: radial-gradient(circle at 50% 40%, rgba(86, 194, 255, 0.08), var(--wdc-bg) 60%);
  backdrop-filter: blur(4px);
}

.splash-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 14px;
  padding: 44px 64px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border-strong);
  border-radius: 14px;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.55);
  min-width: 360px;
}

.splash-logo {
  font-family: 'JetBrains Mono', monospace;
  font-size: 2rem;
  font-weight: 800;
  color: var(--wdc-accent);
  letter-spacing: 0.1em;
  line-height: 1;
}

.splash-title {
  font-size: 1.1rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: -0.01em;
}

.splash-progress-wrap {
  width: 320px;
  display: flex;
  align-items: center;
  gap: 10px;
}

.splash-progress-track {
  flex: 1;
  height: 6px;
  background: var(--wdc-border);
  border-radius: 3px;
  overflow: hidden;
}

.splash-progress-bar {
  height: 100%;
  background: var(--wdc-accent);
  border-radius: 3px;
  transition: width 0.3s ease;
}

.splash-pct {
  font-size: 11px;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-3);
  min-width: 32px;
  text-align: right;
}

.splash-stage {
  font-size: 14px;
  font-weight: 500;
  color: var(--wdc-text);
}

.splash-message {
  font-size: 12px;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
  max-width: 400px;
  text-align: center;
  min-height: 14px;
}

.splash-error {
  font-size: 12px;
  color: var(--el-color-danger);
  max-width: 360px;
  text-align: center;
  word-break: break-word;
}

.splash-retry-btn {
  margin-top: 4px;
  padding: 6px 18px;
  font-size: 12px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  color: var(--wdc-text);
  cursor: pointer;
}

.splash-retry-btn:hover {
  background: var(--wdc-surface-3, var(--wdc-surface-2));
}
</style>
