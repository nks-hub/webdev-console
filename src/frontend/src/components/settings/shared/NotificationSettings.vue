<template>
  <div class="notification-settings">
    <p class="notification-desc">{{ t('osNotifications.settings.description') }}</p>

    <div class="notification-row">
      <div class="notification-text">
        <span id="notify-master-label" class="notification-label">{{ t('osNotifications.settings.masterLabel') }}</span>
        <span class="notification-hint">{{ t('osNotifications.settings.masterHint') }}</span>
      </div>
      <el-switch
        :model-value="prefs.enabled"
        :disabled="!loaded"
        aria-labelledby="notify-master-label"
        data-testid="notify-master"
        @update:model-value="onMaster"
      />
    </div>

    <div class="notification-row">
      <div class="notification-text">
        <span id="notify-background-label" class="notification-label">{{ t('osNotifications.settings.backgroundOnlyLabel') }}</span>
        <span class="notification-hint">{{ t('osNotifications.settings.backgroundOnlyHint') }}</span>
      </div>
      <el-switch
        :model-value="prefs.backgroundOnly"
        :disabled="!loaded || !prefs.enabled"
        aria-labelledby="notify-background-label"
        data-testid="notify-background-only"
        @update:model-value="onBackgroundOnly"
      />
    </div>

    <div class="notification-channels" :class="{ muted: !prefs.enabled }">
      <div class="notification-channels-title">{{ t('osNotifications.settings.channelsTitle') }}</div>
      <div v-for="ch in notificationChannels" :key="ch" class="notification-row">
        <div class="notification-text">
          <span :id="`notify-ch-${ch}`" class="notification-label">{{ t(`osNotifications.channels.${ch}.label`) }}</span>
          <span class="notification-hint">{{ t(`osNotifications.channels.${ch}.description`) }}</span>
        </div>
        <el-switch
          :model-value="prefs.channels[ch]"
          :disabled="!loaded || !prefs.enabled"
          :aria-labelledby="`notify-ch-${ch}`"
          :data-testid="`notify-channel-${ch}`"
          @update:model-value="(v: string | number | boolean) => onChannel(ch, v === true)"
        />
      </div>
      <p class="notification-hint">{{ t('osNotifications.settings.throttleHint') }}</p>
    </div>

    <div class="notification-actions">
      <el-button size="small" plain :loading="testing" data-testid="notify-test" @click="onTest">
        {{ t('osNotifications.settings.testBtn') }}
      </el-button>
      <span class="notification-hint">{{ t('osNotifications.settings.platformHint') }}</span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import {
  hasNativeNotifications,
  loadNotificationPrefs,
  notificationChannels,
  notificationPrefs as prefs,
  notificationPrefsLoaded as loaded,
  sendTestNotification,
  updateNotificationPrefs,
  type NotificationChannel,
  type NotificationPrefs,
} from '../../../services/osNotifications'

/**
 * Settings → Notifications. Shared by the Simple grid and the Advanced
 * SettingsShell section. Changes apply immediately (no Save step) because
 * they are stored by the Electron main process, not the daemon.
 */
const props = defineProps<{
  t: (key: string, params?: Record<string, unknown>) => string
}>()

const testing = ref(false)

onMounted(() => { void loadNotificationPrefs() })

async function save(patch: Partial<NotificationPrefs>): Promise<void> {
  try {
    await updateNotificationPrefs(patch)
  } catch {
    ElMessage.error(props.t('osNotifications.settings.saveFailed'))
  }
}

function onMaster(value: string | number | boolean): void {
  void save({ enabled: value === true })
}

function onBackgroundOnly(value: string | number | boolean): void {
  void save({ backgroundOnly: value === true })
}

function onChannel(channel: NotificationChannel, enabled: boolean): void {
  void save({ channels: { ...prefs.value.channels, [channel]: enabled } })
}

async function onTest(): Promise<void> {
  if (!hasNativeNotifications()) {
    ElMessage.info(props.t('osNotifications.settings.testBrowser'))
    return
  }
  testing.value = true
  try {
    const result = await sendTestNotification(
      props.t('osNotifications.settings.testTitle'),
      props.t('osNotifications.settings.testBody'),
    )
    if (result === 'shown') ElMessage.success(props.t('osNotifications.settings.testShown'))
    else if (result === 'unsupported') ElMessage.warning(props.t('osNotifications.settings.testUnsupported'))
    else ElMessage.error(props.t('osNotifications.settings.testFailed'))
  } finally {
    testing.value = false
  }
}
</script>

<style scoped>
.notification-settings {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-3);
}
.notification-desc {
  margin: 0;
  font-size: 0.85rem;
  color: var(--wdc-text-2);
  line-height: 1.5;
}
.notification-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-4);
  padding: var(--wdc-space-2) 0;
}
.notification-text {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.notification-label {
  font-weight: 600;
  color: var(--wdc-text);
}
.notification-hint {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  line-height: 1.4;
}
.notification-channels {
  display: flex;
  flex-direction: column;
  border-top: 1px solid var(--wdc-border);
  padding-top: var(--wdc-space-3);
}
.notification-channels .notification-row + .notification-row {
  border-top: 1px solid var(--wdc-border);
}
.notification-channels.muted .notification-text {
  opacity: 0.6;
}
/* Same look as the section headings on the General tab. */
.notification-channels-title {
  font-size: 0.82rem;
  font-weight: 600;
  font-family: var(--wdc-font-display);
  color: var(--wdc-text-2);
  margin-bottom: var(--wdc-space-1);
}
.notification-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--wdc-space-3);
  border-top: 1px solid var(--wdc-border);
  padding-top: var(--wdc-space-3);
}
</style>
