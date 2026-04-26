<template>
  <div class="site-tab">
    <!-- Loading state -->
    <div v-if="loading" class="site-tab-loading">
      <el-icon class="is-loading"><Loading /></el-icon>
      <span>Loading deploy state…</span>
    </div>

    <!-- Empty state — no deploy.neon yet. Dual CTA so power users with an
         existing config aren't forced through the wizard. -->
    <div v-else-if="!hasConfig && !wizardOpen" class="site-tab-empty">
      <h3>Deploy {{ domain }}</h3>
      <p class="muted">
        Configure deploy targets so you can publish releases from here without
        leaving wdc. We'll generate a <code class="mono">deploy.neon</code> in
        your project root (committable to git) plus a gitignored
        <code class="mono">deploy.local.neon</code> for secrets.
      </p>
      <ul class="site-tab-points">
        <li>Zero-downtime atomic symlink switch</li>
        <li>Auto-rollback on health-check failure</li>
        <li>Multi-host (production / staging) with per-host config</li>
      </ul>
      <div class="site-tab-actions">
        <el-button type="primary" size="large" @click="wizardOpen = true">Start setup wizard</el-button>
        <el-button size="large" plain @click="$emit('import-config')">Import existing deploy.neon</el-button>
      </div>
      <p class="site-tab-foot">
        <el-icon><InfoFilled /></el-icon>
        First-time setup takes about 3 minutes.
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
      <el-tab-pane name="deploy" label="Deploy">
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

      <el-tab-pane name="groups" label="Groups">
        <DeployGroupHistoryTable :domain="domain" />
      </el-tab-pane>

      <el-tab-pane name="settings" label="Settings">
        <DeploySettingsPanel :domain="domain" />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { InfoFilled, Loading } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'
import DeploySetupWizard from './DeploySetupWizard.vue'
import DeployCommandCenter from './DeployCommandCenter.vue'
import DeploySettingsPanel from './DeploySettingsPanel.vue'
import DeployGroupHistoryTable from './DeployGroupHistoryTable.vue'
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
    ElMessage.warning(`Could not load deploy state: ${(e as Error).message}`)
  } finally {
    loading.value = false
  }
}

async function refreshHistory(): Promise<void> {
  history.value = await deployStore.refreshHistory(props.domain, 50)
}

function rerunPreflight(): void {
  ElMessage.info('Preflight re-run coming in next commit')
}

function onWizardDone(_form: unknown): void {
  wizardOpen.value = false
  ElMessage.info('Config persistence wiring coming in next commit')
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
