<template>
  <div class="site-tab">
    <!-- Loading state -->
    <div v-if="loading" class="site-tab-loading">
      <el-icon class="is-loading"><Loading /></el-icon>
      <span>{{ t('deploy.loading') }}</span>
    </div>

    <!-- Empty state — no deploy.neon yet. Dual CTA so power users with an
         existing config aren't forced through the wizard. -->
    <div v-else-if="!hasConfig && !wizardOpen" class="site-tab-empty">
      <h3>{{ t('deploy.empty.title', { domain }) }}</h3>
      <i18n-t keypath="deploy.empty.description" tag="p" class="muted">
        <template #neon><code class="mono">deploy.neon</code></template>
        <template #localNeon><code class="mono">deploy.local.neon</code></template>
      </i18n-t>
      <ul class="site-tab-points">
        <li>{{ t('deploy.empty.bullets.zeroDowntime') }}</li>
        <li>{{ t('deploy.empty.bullets.autoRollback') }}</li>
        <li>{{ t('deploy.empty.bullets.multiHost') }}</li>
      </ul>
      <div class="site-tab-actions">
        <el-button type="primary" size="large" @click="wizardOpen = true">
          {{ t('deploy.empty.startWizard') }}
        </el-button>
        <el-button size="large" plain @click="$emit('import-config')">
          {{ t('deploy.empty.importExisting') }}
        </el-button>
      </div>
      <p class="site-tab-foot">
        <el-icon><InfoFilled /></el-icon>
        {{ t('deploy.empty.timeEstimate') }}
      </p>
    </div>

    <DeploySetupWizard
      v-else-if="wizardOpen"
      :domain="domain"
      @done="onWizardDone"
      @cancel="wizardOpen = false"
    />

    <!-- Main view: Deploy + Settings sub-tabs -->
    <el-tabs
      v-else
      v-model="activeTab"
      class="deploy-sub-tabs"
    >
      <el-tab-pane name="deploy" :label="t('deploy.subTabs.deploy')">
        <DeployCommandCenter
          :domain="domain"
          :hosts="hosts"
          :history="history"
          :diff="diff"
          :checks="checks"
          :checks-loading="checksLoading"
          @rerun-preflight="rerunPreflight"
          @refresh-history="refreshHistory"
        />
      </el-tab-pane>

      <el-tab-pane name="releases" :label="t('deploy.subTabs.releases')">
        <DeployReleasesTable :entries="history" @refresh="refreshHistory" />
      </el-tab-pane>

      <el-tab-pane name="groups" :label="t('deploy.subTabs.groups')">
        <DeployGroupHistoryTable :domain="domain" />
      </el-tab-pane>

      <el-tab-pane name="settings" :label="t('deploy.subTabs.settings')">
        <DeploySettingsPanel :domain="domain" />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { InfoFilled, Loading } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'

const { t } = useI18n()
import DeploySetupWizard from './DeploySetupWizard.vue'
import DeployCommandCenter from './DeployCommandCenter.vue'
import DeploySettingsPanel from './DeploySettingsPanel.vue'
import DeployGroupHistoryTable from './DeployGroupHistoryTable.vue'
import DeployReleasesTable from './DeployReleasesTable.vue'
import type { DeployHistoryEntryDto } from '../../api/deploy'
import type { DeployDiff } from './DiffSummary.vue'
import type { PreflightCheck } from './PreflightChecklist.vue'

const props = defineProps<{ domain: string }>()
defineEmits<{ 'import-config': [] }>()

const deployStore = useDeployStore()
const loading = ref(true)
const hasConfig = ref(false)
const wizardOpen = ref(false)

// Default tab is "deploy" to preserve existing UX
const activeTab = ref<'deploy' | 'settings'>('deploy')

const hosts = ref<string[]>([])
const history = ref<DeployHistoryEntryDto[]>([])
const diff = ref<DeployDiff | null>(null)
const checks = ref<PreflightCheck[]>([
  { name: 'git_status', state: 'pending', message: 'Working tree status' },
  { name: 'php_lint', state: 'pending', message: 'php -l on changed files' },
  { name: 'di_container', state: 'pending', message: 'Nette DI validate' },
  { name: 'schema_validate', state: 'pending', message: 'Doctrine schema check' },
  { name: 'manifest_check', state: 'pending', message: 'Vite manifest present' },
])
const checksLoading = ref(false)

onMounted(async () => {
  await refreshAll()
})

async function refreshAll(): Promise<void> {
  loading.value = true
  try {
    const entries = await deployStore.refreshHistory(props.domain, 50)
    history.value = entries
    // Hosts inferred from history rows (until a config-list endpoint exists)
    hosts.value = Array.from(new Set(entries.map(e => e.host)))
    hasConfig.value = entries.length > 0 || hosts.value.length > 0
  } catch (e) {
    // 404 = "no deploy.neon yet" — that's the empty-state path, NOT an error
    // worth toasting. Same when the entire deploy subsystem is disabled
    // (Phase 7.1a `deploy.enabled=false` → middleware 404s plugin routes).
    // Both cases land in the empty-state UI which has its own CTA.
    const msg = (e as Error).message
    if (msg.includes('HTTP 404') || msg.includes('deploy_disabled')) {
      hasConfig.value = false
    } else {
      ElMessage.warning(t('deploy.errors.loadFailed', { error: msg }))
    }
  } finally {
    loading.value = false
  }
}

async function refreshHistory(): Promise<void> {
  history.value = await deployStore.refreshHistory(props.domain, 50)
}

function rerunPreflight(): void {
  ElMessage.info(t('deploy.placeholders.preflightRerun'))
}

function onWizardDone(_form: unknown): void {
  wizardOpen.value = false
  ElMessage.info(t('deploy.placeholders.configPersistence'))
  void refreshAll()
}
</script>

<style scoped>
.site-tab { display: flex; flex-direction: column; gap: 24px; padding: 8px 0; }
.site-tab-loading { display: flex; align-items: center; gap: 8px; padding: 32px; color: var(--el-text-color-secondary); }
.site-tab-empty { max-width: 640px; padding: 16px 0; display: flex; flex-direction: column; gap: 12px; }
.site-tab-empty h3 { margin: 0; }
.site-tab-points { display: flex; flex-direction: column; gap: 6px; padding-left: 20px; }
.site-tab-actions { display: flex; gap: 12px; margin-top: 8px; }
.site-tab-foot { display: flex; align-items: center; gap: 6px; color: var(--el-text-color-secondary); font-size: 13px; }
.muted { color: var(--el-text-color-secondary); }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }

.deploy-sub-tabs :deep(.el-tabs__content) {
  padding: 16px 0 0;
}
</style>
