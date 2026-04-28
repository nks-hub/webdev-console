<template>
  <div v-if="visible" class="mcp-onboarding-panel">
    <div class="onboarding-header">
      <div class="onboarding-title">
        🚀 {{ t('mcpOnboarding.title') }}
      </div>
      <el-button size="small" link @click="dismissed = true">
        {{ t('mcpOnboarding.dismiss') }}
      </el-button>
    </div>
    <p class="onboarding-hint muted">{{ t('mcpOnboarding.hint') }}</p>

    <div class="profile-cards">
      <div
        v-for="profile in profiles"
        :key="profile.id"
        class="profile-card"
        :class="{ pending: pending === profile.id, recommended: profile.id === 'balanced' }"
        role="button"
        tabindex="0"
        :aria-label="t(`mcpOnboarding.profiles.${profile.id}.name`) + ' — ' + t(`mcpOnboarding.profiles.${profile.id}.desc`)"
        @click="apply(profile)"
        @keydown.enter.prevent="apply(profile)"
        @keydown.space.prevent="apply(profile)"
      >
        <div class="profile-icon">{{ profile.icon }}</div>
        <div class="profile-name">
          {{ t(`mcpOnboarding.profiles.${profile.id}.name`) }}
          <el-tag v-if="profile.id === 'balanced'" type="success" size="small" effect="plain" class="recommended-tag">
            {{ t('mcpOnboarding.recommended') }}
          </el-tag>
        </div>
        <div class="profile-desc muted">
          {{ t(`mcpOnboarding.profiles.${profile.id}.desc`) }}
        </div>
        <ul class="profile-bullets">
          <li v-for="(b, idx) in tBullets(profile.id)" :key="idx">{{ b }}</li>
        </ul>
        <el-button
          size="small"
          :type="profile.id === 'balanced' ? 'primary' : 'default'"
          :loading="pending === profile.id"
          :disabled="pending !== null && pending !== profile.id"
          class="profile-cta"
        >
          {{ t(`mcpOnboarding.profiles.${profile.id}.cta`) }}
        </el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { createMcpGrant, listMcpGrants, type McpGrantCreateBody } from '../../api/daemon'

const { t } = useI18n()

const dismissed = ref(false)
const grantsCount = ref<number | null>(null)
const pending = ref<string | null>(null)

const ONBOARDING_KEY = 'mcp.onboarding.dismissed.v1'

interface ProfileDefinition {
  id: 'minimal' | 'balanced' | 'permissive' | 'custom'
  icon: string
  grants: McpGrantCreateBody[]
}

const profiles: ProfileDefinition[] = [
  {
    id: 'minimal',
    icon: '🛡️',
    grants: [], // no grants — every destructive op asks
  },
  {
    id: 'balanced',
    icon: '⚖️',
    grants: [
      // Auto-approve only reversible deploy/rollback (no destructive
      // restore / db_drop / settings_write).
      {
        scopeType: 'always',
        scopeValue: null,
        kindPattern: 'deploy',
        targetPattern: '*',
        grantedBy: 'onboarding-balanced',
        note: 'Auto-approve deploy on any site (reversible)',
      },
      {
        scopeType: 'always',
        scopeValue: null,
        kindPattern: 'rollback',
        targetPattern: '*',
        grantedBy: 'onboarding-balanced',
        note: 'Auto-approve rollback on any site (reversible)',
      },
    ],
  },
  {
    id: 'permissive',
    icon: '🚀',
    grants: [
      {
        scopeType: 'always',
        scopeValue: null,
        kindPattern: '*',
        targetPattern: '*',
        grantedBy: 'onboarding-permissive',
        note: 'Auto-approve everything — explicit operator opt-in',
      },
    ],
  },
  {
    id: 'custom',
    icon: '🎯',
    grants: [],
  },
]

function tBullets(id: string): string[] {
  const raw = t(`mcpOnboarding.profiles.${id}.bullets`)
  return Array.isArray(raw) ? (raw as unknown as string[]) : []
}

const visible = computed(() => {
  if (dismissed.value) return false
  if (grantsCount.value === null) return false
  return grantsCount.value === 0
})

async function refresh(): Promise<void> {
  try {
    const data = await listMcpGrants()
    grantsCount.value = data.entries.length
  } catch {
    grantsCount.value = null
  }
}

async function apply(profile: ProfileDefinition): Promise<void> {
  pending.value = profile.id
  try {
    if (profile.id === 'custom') {
      // Don't create anything — just navigate operator to Rules tab.
      window.location.hash = '/mcp/grants'
      dismiss()
      return
    }
    if (profile.grants.length === 0 && profile.id === 'minimal') {
      // Minimal = explicit "no grants, ask every time" — just dismiss.
      ElMessage.success(t('mcpOnboarding.minimalApplied'))
      dismiss()
      return
    }
    let okCount = 0
    for (const grant of profile.grants) {
      try {
        await createMcpGrant(grant)
        okCount++
      } catch (err) {
        console.warn('[McpOnboardingPanel] create failed:', err)
      }
    }
    ElMessage.success(t('mcpOnboarding.applied', { n: okCount, profile: t(`mcpOnboarding.profiles.${profile.id}.name`) }))
    dismiss()
    void refresh()
  } finally {
    pending.value = null
  }
}

function dismiss(): void {
  dismissed.value = true
  try { localStorage.setItem(ONBOARDING_KEY, '1') } catch { /* ignore */ }
}

onMounted(() => {
  try {
    if (localStorage.getItem(ONBOARDING_KEY) === '1') dismissed.value = true
  } catch { /* ignore */ }
  void refresh()
})
</script>

<style scoped>
.mcp-onboarding-panel {
  border: 2px solid var(--el-color-primary-light-5);
  background: linear-gradient(135deg, var(--el-color-primary-light-9), var(--el-fill-color-light));
  border-radius: 8px;
  padding: 16px 20px;
  margin-bottom: 12px;
}
.onboarding-header {
  display: flex; align-items: center; justify-content: space-between;
  margin-bottom: 4px;
}
.onboarding-title { font-size: 16px; font-weight: 600; }
.onboarding-hint { margin: 0 0 16px 0; font-size: 13px; }
.profile-cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
}
.profile-card {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  padding: 14px;
  cursor: pointer;
  transition: all 0.15s;
  display: flex; flex-direction: column; gap: 6px;
  position: relative;
}
.profile-card:hover {
  border-color: var(--el-color-primary);
  transform: translateY(-2px);
  box-shadow: 0 2px 8px var(--el-color-primary-light-7);
}
.profile-card.recommended {
  border-color: var(--el-color-success-light-5);
}
.profile-card.pending { opacity: 0.7; pointer-events: none; }
.profile-icon { font-size: 28px; }
.profile-name {
  font-size: 14px; font-weight: 600;
  display: flex; align-items: center; gap: 8px;
}
.recommended-tag { font-size: 10px; }
.profile-desc { font-size: 12px; }
.profile-bullets {
  margin: 4px 0 0;
  padding-left: 16px;
  font-size: 11px;
  color: var(--el-text-color-secondary);
}
.profile-bullets li { margin-bottom: 2px; }
.profile-cta { margin-top: auto; align-self: flex-start; }
.muted { color: var(--el-text-color-secondary); }
</style>
