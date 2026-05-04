<template>
  <SettingsCard :title="t('settings.sso.title')">
    <template #meta>
      <span v-if="isAuthenticated" class="sso-status">{{ t('settings.sso.signedIn') }}</span>
    </template>
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
  </SettingsCard>
</template>

<script setup lang="ts">
import SettingsCard from '../shared/SettingsCard.vue'

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
