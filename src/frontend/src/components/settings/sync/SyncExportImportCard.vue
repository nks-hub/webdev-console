<template>
  <SettingsCard :title="t('settings.sync.exportImportTitle')">
    <p class="tab-desc">{{ t('settings.sync.tabDesc') }}</p>
    <div class="sync-actions">
      <el-button size="small" @click="emit('export')">
        <el-icon><Download /></el-icon>
        <span>{{ t('settings.sync.exportFile') }}</span>
      </el-button>
      <el-button size="small" @click="triggerImport">
        <el-icon><Upload /></el-icon>
        <span>{{ t('settings.sync.importFile') }}</span>
      </el-button>
      <input
        ref="importFileInput"
        type="file"
        accept=".json"
        class="file-input"
        @change="emit('import', $event)"
      />
    </div>
  </SettingsCard>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { Download, Upload } from '@element-plus/icons-vue'
import SettingsCard from '../shared/SettingsCard.vue'

defineProps<{
  t: (key: string) => string
}>()

const emit = defineEmits<{
  export: []
  import: [event: Event]
}>()

const importFileInput = ref<HTMLInputElement | null>(null)

function triggerImport(): void {
  importFileInput.value?.click()
}
</script>

<style scoped>
.tab-desc {
  margin: 0 0 12px;
  color: var(--wdc-text-3);
  font-size: 0.85rem;
}

.sync-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.file-input {
  display: none;
}
</style>
