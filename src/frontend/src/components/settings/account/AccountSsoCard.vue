<template>
  <section class="settings-card">
    <header class="settings-card-header">
      <span class="settings-card-title">{{ t('settings.sso.title') }}</span>
      <span v-if="isAuthenticated" class="sso-status">{{ t('settings.sso.signedIn') }}</span>
    </header>
    <div class="settings-card-body">
      <div v-if="isAuthenticated" class="sync-actions sso-column">
        <span class="tab-desc sso-description">
          {{ displayName
            ? t('settings.sso.signedInAs', { who: displayName })
            : t('settings.sso.signedInAt', { url: t('settings.sso.configuredCatalog') }) }}
        </span>
        <el-button size="small" @click="emit('logout')">{{ t('settings.sso.signOut') }}</el-button>
      </div>
      <div v-else class="sync-actions sso-column">
        <p class="tab-desc">{{ t('settings.sso.description') }}</p>
        <div class="sso-actions">
          <el-button
            size="small"
            type="primary"
            :loading="loginPending"
            @click="emit('login')"
          >{{ t('settings.sso.signIn') }}</el-button>
          <span v-if="loginError" class="sso-error">{{ loginError }}</span>
        </div>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
defineProps<{
  t: (key: string, params?: Record<string, unknown>) => string
  isAuthenticated: boolean
  displayName: string | null | undefined
  loginPending: boolean
  loginError: string | null | undefined
}>()

const emit = defineEmits<{
  login: []
  logout: []
}>()
</script>

<style scoped>
.settings-card {
  border: 1px solid var(--wdc-border);
  border-radius: 8px;
  background: var(--wdc-surface-2);
  margin-bottom: 12px;
}

.settings-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  border-bottom: 1px solid var(--wdc-border);
}

.settings-card-title {
  font-weight: 600;
  color: var(--wdc-text-1);
}

.settings-card-body {
  padding: 18px;
}

.sync-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.sso-column {
  flex-direction: column;
  align-items: flex-start;
}

.sso-description {
  margin: 0;
}

.sso-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.sso-status {
  font-size: 0.78rem;
  color: var(--wdc-status-running);
}

.sso-error {
  color: var(--wdc-status-error);
  font-size: 0.78rem;
}

.tab-desc {
  margin: 0 0 12px;
  color: var(--wdc-text-3);
  font-size: 0.85rem;
}
</style>
