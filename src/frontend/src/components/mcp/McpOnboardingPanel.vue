<template>
  <div v-if="visible" class="mcp-onboarding-panel">
    <div class="onboarding-header">
      <div class="onboarding-title">
        {{ t('mcpOnboarding.title') }}
      </div>
      <el-button size="small" text class="dismiss-btn" @click="dismiss">
        {{ t('mcpOnboarding.dismiss') }}
      </el-button>
    </div>
    <p class="onboarding-hint muted">{{ t('mcpOnboarding.hint') }}</p>

    <!-- Only the button applies a profile: a click anywhere on a card used
         to create grants, Full trust included. -->
    <div class="profile-cards">
      <div
        v-for="profile in MCP_PROFILES"
        :key="profile.id"
        class="profile-card"
        :class="{ pending: pending === profile.id, recommended: profile.id === DEFAULT_PROFILE, risky: profile.id === 'permissive' }"
        :data-profile="profile.id"
      >
        <div class="profile-name">
          <el-icon v-if="profile.id === 'permissive'" class="risky-icon" aria-hidden="true"><Warning /></el-icon>
          {{ t(`mcpOnboarding.profiles.${profile.id}.name`) }}
          <el-tag v-if="profile.id === DEFAULT_PROFILE" type="success" size="small" effect="plain" class="recommended-tag">
            {{ t('mcpOnboarding.default') }}
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
          :type="profile.id === DEFAULT_PROFILE ? 'primary' : profile.id === 'permissive' ? 'warning' : 'default'"
          :plain="profile.id === 'permissive'"
          :loading="pending === profile.id"
          :disabled="pending !== null && pending !== profile.id"
          class="profile-cta"
          @click="apply(profile)"
        >
          {{ t(`mcpOnboarding.profiles.${profile.id}.cta`) }}
        </el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Warning } from '@element-plus/icons-vue'
import { createMcpGrant, listMcpGrants, subscribeEventsMap } from '../../api/daemon'
import { DEFAULT_PROFILE, MCP_PROFILES, type McpProfileDefinition } from './mcpProfiles'

const { t, tm, rt } = useI18n()

const dismissed = ref(false)
const grantsCount = ref<number | null>(null)
const pending = ref<string | null>(null)

const ONBOARDING_KEY = 'mcp.onboarding.dismissed.v1'

// Bullets are an array in the locale files: t() cannot return arrays, so
// read the raw messages with tm() and render each with rt().
function tBullets(id: string): string[] {
  const raw: unknown = tm(`mcpOnboarding.profiles.${id}.bullets`)
  if (!Array.isArray(raw)) return []
  return raw.map((m) => (typeof m === 'string' ? m : rt(m as Parameters<typeof rt>[0])))
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

// Full trust lets any AI client do anything without asking, so it has to
// be typed, not clicked.
async function confirmPermissive(): Promise<boolean> {
  const word = t('mcpOnboarding.permissiveWord')
  try {
    await ElMessageBox.prompt(t('mcpOnboarding.permissiveConfirm', { word }), t('mcpOnboarding.profiles.permissive.name'), {
      type: 'warning',
      confirmButtonText: t('mcpOnboarding.profiles.permissive.cta'),
      cancelButtonText: t('common.cancel'),
      inputPlaceholder: word,
      inputValidator: (value: string) => value.trim().toUpperCase() === word.toUpperCase() || t('mcpOnboarding.permissiveMismatch', { word }),
    })
    return true
  } catch {
    return false
  }
}

async function apply(profile: McpProfileDefinition): Promise<void> {
  if (profile.id === 'permissive' && !(await confirmPermissive())) return
  pending.value = profile.id
  try {
    if (profile.id === 'custom') {
      // Don't create anything — just navigate operator to Rules tab.
      window.location.hash = '/mcp/grants'
      dismiss()
      return
    }
    if (profile.grants.length === 0) {
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
    const name = t(`mcpOnboarding.profiles.${profile.id}.name`)
    if (okCount < profile.grants.length) {
      // Keep the panel so the operator can retry; say how far it got.
      ElMessage.error(t('mcpOnboarding.applyFailed', { profile: name, ok: okCount, total: profile.grants.length }))
    } else {
      ElMessage.success(t('mcpOnboarding.applied', { profile: name }))
      dismiss()
    }
    void refresh()
  } finally {
    pending.value = null
  }
}

function dismiss(): void {
  dismissed.value = true
  try { localStorage.setItem(ONBOARDING_KEY, '1') } catch { /* ignore */ }
}

// SSE — if a grant is created/revoked elsewhere (Rules tab, banner
// shortcut, suggested-grants banner), re-evaluate visibility.
let unsubscribe: (() => void) | null = null

onMounted(() => {
  try {
    if (localStorage.getItem(ONBOARDING_KEY) === '1') dismissed.value = true
  } catch { /* ignore */ }
  void refresh()
  unsubscribe = subscribeEventsMap({
    'mcp:grant-changed': () => { void refresh() },
  })
})

onBeforeUnmount(() => {
  if (unsubscribe) unsubscribe()
})
</script>

<style scoped>
.mcp-onboarding-panel {
  border: 1px solid var(--wdc-accent-glow);
  background: var(--wdc-surface);
  border-radius: var(--wdc-radius-lg);
  padding: var(--wdc-space-4-5) var(--wdc-space-5);
  margin-bottom: var(--wdc-space-4);
  box-shadow: var(--wdc-shadow-card);
}
.onboarding-header {
  display: flex; align-items: center; justify-content: space-between;
  margin-bottom: var(--wdc-space-1);
}
.onboarding-title { font-size: 16px; font-weight: 600; }
.onboarding-hint { margin: 0 0 16px 0; font-size: 13px; }
.profile-cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: var(--wdc-space-3);
}
.profile-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: var(--wdc-space-4);
  transition: box-shadow 0.15s, border-color 0.15s;
  display: flex; flex-direction: column; gap: 6px;
  position: relative;
  box-shadow: var(--wdc-shadow-sm);
}
.profile-card:hover {
  border-color: var(--wdc-accent);
  box-shadow: var(--wdc-shadow-card), 0 0 0 1px var(--wdc-accent-glow);
}
.profile-card.risky { border-color: var(--el-color-warning-light-5); }
.risky-icon { color: var(--el-color-warning); }
.profile-card.recommended {
  border-color: var(--el-color-success-light-5);
}
.profile-card.pending { opacity: 0.7; pointer-events: none; }
.profile-name {
  font-size: 14px; font-weight: 600;
  display: flex; align-items: center; gap: 8px;
}
.recommended-tag { font-size: 10px; }
.profile-desc { font-size: 13px; line-height: 1.45; }
.profile-bullets {
  margin: var(--wdc-space-1) 0 0;
  padding-left: var(--wdc-space-4);
  font-size: 12px;
  line-height: 1.4;
  color: var(--el-text-color-secondary);
}
.profile-bullets li { margin-bottom: 2px; }
.profile-cta { margin-top: auto; align-self: flex-start; }
.muted { color: var(--el-text-color-secondary); }
.dismiss-btn {
  min-height: 32px;
  color: var(--wdc-text-2) !important;
  background: var(--wdc-surface-2) !important;
  border: 1px solid var(--wdc-border) !important;
}
.recommended-tag {
  min-height: 24px;
  font-size: 0.72rem;
}

@media (max-width: 640px) {
  .mcp-onboarding-panel {
    padding: var(--wdc-space-3-5);
  }

  .onboarding-header {
    align-items: flex-start;
    gap: var(--wdc-space-2);
  }

  .profile-cards {
    grid-template-columns: 1fr;
  }
}
</style>
