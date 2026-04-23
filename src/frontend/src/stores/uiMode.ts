import { defineStore } from 'pinia'
import { computed } from 'vue'
import { useLocalStorage } from '@vueuse/core'

export type UiMode = 'simple' | 'advanced'

const STORAGE_KEY = 'wdc-ui-mode'

export const useUiModeStore = defineStore('uiMode', () => {
  const _mode = useLocalStorage<UiMode>(STORAGE_KEY, 'advanced')

  const uiMode = computed<UiMode>(() => _mode.value)
  const isSimple = computed(() => _mode.value === 'simple')
  const isAdvanced = computed(() => _mode.value === 'advanced')

  function setUiMode(mode: UiMode) {
    _mode.value = mode
  }

  function toggleMode() {
    _mode.value = _mode.value === 'simple' ? 'advanced' : 'simple'
  }

  return { uiMode, isSimple, isAdvanced, setUiMode, toggleMode }
})
