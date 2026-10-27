<!--
  Catches a page that fails to set up or render and shows a recoverable
  message instead of an empty content area. While the message is shown the
  wrapped <keep-alive> is unmounted, so the broken instance is thrown away
  instead of staying cached and erroring on every later update.
-->
<template>
  <section v-if="failure" class="wdc-page page-failure" role="alert">
    <h1 class="page-title">{{ t('pageError.title') }}</h1>
    <p class="page-sub">{{ t('pageError.lead') }}</p>
    <pre class="page-failure-text mono">{{ failure }}</pre>
    <div class="page-failure-actions">
      <el-button type="primary" @click="failure = null">{{ t('pageError.reload') }}</el-button>
      <el-button @click="copy">{{ t('pageError.copy') }}</el-button>
    </div>
  </section>
  <slot v-else />
</template>

<script setup lang="ts">
import { onErrorCaptured, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'

const { t } = useI18n()
const route = useRoute()
const failure = ref<string | null>(null)

// Only errors that leave a component without a working render are fatal;
// a failed click handler or fetch keeps the page usable and shows its own toast.
// Development builds pass words, production builds a link ending in the error code.
const FATAL = /^(setup function|render function|component update)$|#runtime-(0|1|15)$/

onErrorCaptured((err, _instance, info) => {
  if (FATAL.test(info)) {
    failure.value = err instanceof Error ? `${err.name}: ${err.message}` : String(err)
  }
  // Not returning false: app.config.errorHandler still logs it.
})

// Another page gets a fresh start.
watch(() => route.path, () => { failure.value = null })

async function copy() {
  try {
    await navigator.clipboard.writeText(`${route.fullPath}\n${failure.value ?? ''}`)
    ElMessage.success(t('pageError.copied'))
  } catch {
    ElMessage.error(t('pageError.copyFailed'))
  }
}
</script>

<style scoped>
.page-failure { display: grid; gap: var(--wdc-space-4); justify-items: start; }
.page-title { font-size: 1.728rem; line-height: 1.1; letter-spacing: -0.028em; }
.page-sub { color: var(--wdc-text-2); max-width: 60ch; line-height: 1.5; }
.page-failure-text {
  margin: 0;
  max-width: 100%;
  max-height: 240px;
  overflow: auto;
  padding: var(--wdc-space-3);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-log-bg);
  color: var(--wdc-log-text);
  font-size: 0.78rem;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.page-failure-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); }
.page-failure-actions :deep(.el-button + .el-button) { margin-left: 0; }
</style>
