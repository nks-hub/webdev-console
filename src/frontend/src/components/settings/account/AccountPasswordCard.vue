<template>
  <section class="settings-card">
    <header class="settings-card-header">
      <span class="settings-card-title">{{ title }}</span>
    </header>
    <div class="settings-card-body">
      <p class="tab-desc">{{ t('settings.account.passwordAlt') }}</p>
      <el-form label-position="top" size="small" class="account-form" @submit.prevent="emit('login')">
        <el-form-item :label="t('settings.account.email')">
          <el-input
            :model-value="email"
            placeholder="you@example.com"
            @update:model-value="(value: string) => emit('update:email', value)"
          />
        </el-form-item>
        <el-form-item :label="t('settings.account.password')">
          <el-input
            :model-value="password"
            type="password"
            show-password
            @update:model-value="(value: string) => emit('update:password', value)"
          />
        </el-form-item>
        <div class="sync-actions">
          <el-button type="primary" size="small" :loading="loading" @click="emit('login')">
            {{ t('common.login') }}
          </el-button>
          <el-button size="small" :loading="loading" @click="emit('register')">
            {{ t('common.register') }}
          </el-button>
        </div>
        <div v-if="error" class="hint auth-error">
          {{ error }}
        </div>
      </el-form>
    </div>
  </section>
</template>

<script setup lang="ts">
defineProps<{
  t: (key: string) => string
  title: string
  email: string
  password: string
  loading: boolean
  error: string
}>()

const emit = defineEmits<{
  'update:email': [value: string]
  'update:password': [value: string]
  login: []
  register: []
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

.tab-desc {
  margin: 0 0 12px;
  color: var(--wdc-text-3);
  font-size: 0.85rem;
}

.account-form {
  max-width: 360px;
}

.sync-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.auth-error {
  color: var(--wdc-status-error);
  margin-top: 8px;
}
</style>
