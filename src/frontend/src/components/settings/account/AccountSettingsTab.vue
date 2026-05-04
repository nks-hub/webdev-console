<template>
  <div class="tab-content">
    <AccountSsoCard
      :t="t"
      :is-authenticated="ssoAuthenticated"
      :display-name="ssoDisplayName"
      :login-pending="ssoLoginPending"
      :login-error="ssoLoginError"
      @login="$emit('ssoLogin')"
      @logout="$emit('ssoLogout')"
    />

    <template v-if="isSimple">
      <AccountPasswordCard
        v-if="!accountToken"
        :t="t"
        :title="t('settings.tabs.account')"
        :email="authEmail"
        :password="authPassword"
        :loading="authLoading"
        :error="authError"
        @update:email="$emit('update:authEmail', $event)"
        @update:password="$emit('update:authPassword', $event)"
        @login="$emit('login')"
        @register="$emit('register')"
      />
      <AccountSimpleSyncCard
        v-else
        :t="t"
        :title="t('settings.tabs.account')"
        :email="accountEmail"
        :syncing="syncing"
        :pulling="pulling"
        @push="$emit('push')"
        @pull="$emit('pull')"
        @logout="$emit('logout')"
      />
    </template>

    <template v-if="!isSimple">
      <AccountPasswordCard
        v-if="!accountToken"
        :t="t"
        :title="t('settings.account.passwordTitle')"
        :email="authEmail"
        :password="authPassword"
        :loading="authLoading"
        :error="authError"
        @update:email="$emit('update:authEmail', $event)"
        @update:password="$emit('update:authPassword', $event)"
        @login="$emit('login')"
        @register="$emit('register')"
      />
      <AccountAdvancedSummaryCard
        v-else
        :t="t"
        :email="accountEmail"
        :devices-loading="devicesLoading"
        @refresh-devices="$emit('refreshDevices')"
        @logout="$emit('logout')"
      />

      <AccountDeviceTableCard
        v-if="accountToken"
        :devices="accountDevices"
        :editing-device-name="editingDeviceName"
        :editing-device-value="editingDeviceValue"
        :pushing-to="pushingTo"
        :unlinking-device="unlinkingDevice"
        @update:editing-device-name="$emit('update:editingDeviceName', $event)"
        @update:editing-device-value="$emit('update:editingDeviceValue', $event)"
        @start-edit-name="$emit('startEditName', $event)"
        @save-name="$emit('saveName', $event)"
        @push-config="$emit('pushConfig', $event)"
        @unlink="$emit('unlink', $event)"
      />
    </template>
  </div>
</template>

<script setup lang="ts">
import type { DeviceInfo as CatalogDeviceInfo } from '../../../api/daemon'
import AccountAdvancedSummaryCard from './AccountAdvancedSummaryCard.vue'
import AccountDeviceTableCard from './AccountDeviceTableCard.vue'
import AccountPasswordCard from './AccountPasswordCard.vue'
import AccountSimpleSyncCard from './AccountSimpleSyncCard.vue'
import AccountSsoCard from './AccountSsoCard.vue'

defineProps<{
  t: (key: string, params?: Record<string, unknown>) => string
  isSimple: boolean
  ssoAuthenticated: boolean
  ssoDisplayName: string | null | undefined
  ssoLoginPending: boolean
  ssoLoginError: string | null | undefined
  accountToken: string
  authEmail: string
  authPassword: string
  authLoading: boolean
  authError: string
  accountEmail: string
  syncing: boolean
  pulling: boolean
  devicesLoading: boolean
  accountDevices: CatalogDeviceInfo[]
  editingDeviceName: string | null
  editingDeviceValue: string
  pushingTo: string | null
  unlinkingDevice: string | null
}>()

defineEmits<{
  ssoLogin: []
  ssoLogout: []
  'update:authEmail': [value: string]
  'update:authPassword': [value: string]
  login: []
  register: []
  push: []
  pull: []
  logout: []
  refreshDevices: []
  'update:editingDeviceName': [value: string | null]
  'update:editingDeviceValue': [value: string]
  startEditName: [row: CatalogDeviceInfo]
  saveName: [row: CatalogDeviceInfo]
  pushConfig: [deviceId: string]
  unlink: [row: CatalogDeviceInfo]
}>()
</script>
