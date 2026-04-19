<template>
  <div class="site-composer">
    <!-- Loading skeleton -->
    <div v-if="loading" class="composer-loading">
      <el-skeleton :rows="4" animated />
    </div>

    <!-- Error loading status -->
    <el-alert
      v-else-if="loadError"
      :title="loadError"
      type="error"
      show-icon
      :closable="false"
      class="composer-alert"
    />

    <!-- No composer.json detected -->
    <div v-else-if="status && !status.hasComposerJson" class="composer-empty">
      <el-empty :description="$t('sites.composer.noProject')">
        <template #extra>
          <p class="composer-empty-hint">{{ $t('sites.composer.initHint') }}</p>
          <el-tag type="info" class="composer-init-cmd">
            composer init
          </el-tag>
        </template>
      </el-empty>
    </div>

    <!-- Main composer panel -->
    <div v-else-if="status" class="composer-panel">
      <!-- Framework auto-install suggestion banner -->
      <el-alert
        v-if="status.installSuggestion && !isBannerDismissed"
        :title="$t('sites.composer.frameworkDetected', { framework: status.installSuggestion.framework })"
        type="warning"
        show-icon
        :closable="false"
        class="composer-framework-banner"
      >
        <template #default>
          <p class="banner-description">{{ $t('sites.composer.installPrompt') }}</p>
          <div class="banner-actions">
            <el-button type="primary" size="small" :loading="running" @click="runInstall">
              {{ $t('sites.composer.installNow') }}
            </el-button>
            <el-button size="small" @click="dismissBanner">
              {{ $t('sites.composer.dismiss') }}
            </el-button>
          </div>
        </template>
      </el-alert>

      <!-- Status summary -->
      <el-card class="composer-card" shadow="never">
        <template #header>
          <span class="card-title">{{ $t('sites.composer.tabLabel') }}</span>
        </template>

        <div class="status-row">
          <div class="status-item">
            <span class="status-label">composer.json</span>
            <el-tag type="success" size="small">
              <el-icon><Check /></el-icon> present
            </el-tag>
          </div>

          <div class="status-item">
            <span class="status-label">{{ $t('sites.composer.lockStatus') }}</span>
            <el-tag :type="status.hasLock ? 'success' : 'warning'" size="small">
              <el-icon v-if="status.hasLock"><Check /></el-icon>
              <el-icon v-else><Warning /></el-icon>
              {{ status.hasLock ? 'present' : 'missing' }}
            </el-tag>
          </div>

          <div class="status-item">
            <span class="status-label">PHP</span>
            <el-tag v-if="status.phpVersion" type="info" size="small">
              {{ status.phpVersion }}
            </el-tag>
            <span v-else class="status-na">—</span>
          </div>

          <div class="status-item">
            <span class="status-label">{{ $t('sites.composer.packageCount', { count: status.packages.length }) }}</span>
          </div>
        </div>

        <!-- Package chips -->
        <div v-if="status.packages.length > 0" class="package-list">
          <el-tag
            v-for="pkg in status.packages"
            :key="pkg"
            size="small"
            class="pkg-tag"
          >
            {{ pkg }}
          </el-tag>
        </div>
      </el-card>

      <!-- Actions -->
      <el-card class="composer-card" shadow="never">
        <div class="actions-row">
          <el-button
            type="primary"
            :loading="running"
            @click="runInstall"
          >
            {{ $t('sites.composer.install') }}
          </el-button>

          <el-button
            :loading="running"
            @click="showRequireDialog = true"
          >
            {{ $t('sites.composer.require') }}
          </el-button>

          <el-button
            circle
            :icon="Refresh"
            :loading="loading"
            :title="$t('common.refresh')"
            @click="loadStatus"
          />
        </div>
      </el-card>

      <!-- Last command output -->
      <el-card v-if="lastResult" class="composer-card composer-output" shadow="never">
        <template #header>
          <div class="output-header">
            <span class="card-title">{{ $t('sites.composer.lastOutput') }}</span>
            <el-tag
              :type="lastResult.exitCode === 0 ? 'success' : 'danger'"
              size="small"
            >
              {{ $t('sites.composer.exitCode') }}: {{ lastResult.exitCode }}
            </el-tag>
          </div>
        </template>

        <el-collapse v-model="outputOpen">
          <el-collapse-item v-if="lastResult.stdout" name="stdout" title="stdout">
            <pre class="output-pre">{{ lastResult.stdout }}</pre>
          </el-collapse-item>
          <el-collapse-item v-if="lastResult.stderr" name="stderr" title="stderr">
            <pre class="output-pre output-pre--err">{{ lastResult.stderr }}</pre>
          </el-collapse-item>
        </el-collapse>
      </el-card>
    </div>

    <!-- Require package dialog -->
    <el-dialog
      v-model="showRequireDialog"
      :title="$t('sites.composer.require')"
      width="440px"
      :close-on-click-modal="!running"
    >
      <el-form @submit.prevent="runRequire">
        <el-form-item :label="$t('sites.composer.packageName')">
          <el-input
            v-model="requirePackage"
            :placeholder="$t('sites.composer.packageNameHint')"
            :disabled="running"
            autofocus
            @keyup.enter="runRequire"
          />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="showRequireDialog = false" :disabled="running">
          {{ $t('common.cancel') }}
        </el-button>
        <el-button
          type="primary"
          :loading="running"
          :disabled="!requirePackage.trim()"
          @click="runRequire"
        >
          {{ $t('sites.composer.require') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Check, Warning, Refresh } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { composerStatus, composerInstall, composerRequire } from '../../api/daemon'
import type { ComposerStatus, ComposerCommandResult } from '../../api/types'

const props = defineProps<{ domain: string }>()

const { t } = useI18n()

const loading = ref(false)
const loadError = ref<string | null>(null)
const status = ref<ComposerStatus | null>(null)

const running = ref(false)
const lastResult = ref<ComposerCommandResult | null>(null)
const outputOpen = ref<string[]>(['stdout', 'stderr'])

const showRequireDialog = ref(false)
const requirePackage = ref('')

const DISMISS_KEY = computed(() => `wdc-composer-dismiss-${props.domain}`)
const isBannerDismissed = ref(false)

function loadDismissState(): void {
  isBannerDismissed.value = localStorage.getItem(DISMISS_KEY.value) === '1'
}

function dismissBanner(): void {
  localStorage.setItem(DISMISS_KEY.value, '1')
  isBannerDismissed.value = true
}

async function loadStatus(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    status.value = await composerStatus(props.domain)
  } catch (err: unknown) {
    loadError.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}

async function runInstall(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('sites.composer.install'),
      { type: 'info', confirmButtonText: t('common.apply'), cancelButtonText: t('common.cancel') }
    )
  } catch {
    return
  }

  running.value = true
  try {
    const result = await composerInstall(props.domain)
    lastResult.value = result
    outputOpen.value = result.exitCode !== 0 ? ['stdout', 'stderr'] : ['stdout']
    if (result.exitCode === 0) {
      ElMessage.success(t('sites.composer.success'))
      dismissBanner()
      await loadStatus()
    } else {
      ElMessage.error(t('sites.composer.failed', { code: result.exitCode }))
    }
  } catch (err: unknown) {
    ElMessage.error(err instanceof Error ? err.message : String(err))
  } finally {
    running.value = false
  }
}

async function runRequire(): Promise<void> {
  const pkg = requirePackage.value.trim()
  if (!pkg) return

  running.value = true
  try {
    const result = await composerRequire(props.domain, pkg)
    lastResult.value = result
    outputOpen.value = result.exitCode !== 0 ? ['stdout', 'stderr'] : ['stdout']
    if (result.exitCode === 0) {
      ElMessage.success(t('sites.composer.success'))
      showRequireDialog.value = false
      requirePackage.value = ''
      await loadStatus()
    } else {
      ElMessage.error(t('sites.composer.failed', { code: result.exitCode }))
    }
  } catch (err: unknown) {
    ElMessage.error(err instanceof Error ? err.message : String(err))
  } finally {
    running.value = false
  }
}

onMounted(() => {
  loadDismissState()
  loadStatus()
})
</script>

<style scoped>
.site-composer {
  padding: 16px;
}

.composer-loading {
  padding: 24px;
}

.composer-alert {
  margin-bottom: 16px;
}

.composer-empty {
  padding: 40px 0;
  text-align: center;
}

.composer-empty-hint {
  color: var(--el-text-color-secondary);
  margin: 8px 0;
  font-size: 13px;
}

.composer-init-cmd {
  font-family: monospace;
  font-size: 13px;
}

.composer-panel {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.composer-card {
  border-radius: 8px;
}

.card-title {
  font-weight: 600;
  font-size: 14px;
}

.status-row {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  align-items: center;
  margin-bottom: 12px;
}

.status-item {
  display: flex;
  align-items: center;
  gap: 8px;
}

.status-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.status-na {
  color: var(--el-text-color-placeholder);
}

.package-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}

.pkg-tag {
  font-family: monospace;
  font-size: 12px;
}

.actions-row {
  display: flex;
  gap: 8px;
  align-items: center;
}

.output-header {
  display: flex;
  align-items: center;
  gap: 12px;
}

.output-pre {
  font-family: monospace;
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-all;
  background: var(--el-fill-color-light);
  border-radius: 4px;
  padding: 10px;
  margin: 0;
  max-height: 300px;
  overflow-y: auto;
}

.output-pre--err {
  background: var(--el-color-danger-light-9);
  color: var(--el-color-danger);
}

.composer-framework-banner {
  align-items: flex-start;
}

.banner-description {
  margin: 0 0 8px;
  font-size: 13px;
  color: var(--el-text-color-regular);
}

.banner-actions {
  display: flex;
  gap: 8px;
}
</style>
