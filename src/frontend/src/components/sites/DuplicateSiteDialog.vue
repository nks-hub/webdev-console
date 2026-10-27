<template>
  <el-dialog v-model="open" :title="$t('sites.card.duplicateTitle')" width="480px" @opened="focusInput" @closed="busy = false">
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="$t('sites.card.duplicateNewDomain')">
        <el-input ref="input" v-model="newDomain" spellcheck="false" />
      </el-form-item>
      <el-form-item :label="$t('sites.card.duplicateCopyFiles')">
        <el-radio-group v-model="copyFiles">
          <el-radio value="all">{{ $t('sites.card.copyFilesAll') }}</el-radio>
          <el-radio value="top">{{ $t('sites.card.copyFilesTop') }}</el-radio>
          <el-radio value="empty">{{ $t('sites.card.copyFilesEmpty') }}</el-radio>
        </el-radio-group>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button :disabled="busy" @click="open = false">{{ $t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="busy" :disabled="!newDomain.trim()" @click="submit">{{ $t('sites.card.duplicate') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { duplicateSite } from '../../api/daemon'
import { useSitesStore } from '../../stores/sites'
import { errorMessage } from '../../utils/errors'

const props = defineProps<{ source: string }>()
const open = defineModel<boolean>({ required: true })
const emit = defineEmits<{ (e: 'duplicated', domain: string): void }>()

const { t } = useI18n()
const sitesStore = useSitesStore()
const input = ref<{ focus?: () => void } | null>(null)
const newDomain = ref('')
const copyFiles = ref<'all' | 'top' | 'empty'>('all')
const busy = ref(false)

watch(open, (isOpen) => {
  if (!isOpen) return
  newDomain.value = `copy-of-${props.source}`
  copyFiles.value = 'all'
})

function focusInput() { void nextTick(() => input.value?.focus?.()) }

async function submit() {
  const domain = newDomain.value.trim().toLowerCase()
  if (!domain || busy.value) return
  busy.value = true
  try {
    await duplicateSite(props.source, domain, copyFiles.value)
    ElMessage.success(t('sites.card.duplicated', { name: domain }))
    await sitesStore.load()
    open.value = false
    emit('duplicated', domain)
  } catch (e) {
    ElMessage.error(errorMessage(e))
  } finally {
    busy.value = false
  }
}
</script>
