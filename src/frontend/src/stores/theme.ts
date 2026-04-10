import { defineStore } from 'pinia'
import { ref, watch, computed } from 'vue'

export type ThemeMode = 'dark' | 'light' | 'system'

const STORAGE_KEY = 'nks-wdc-theme'

function getSystemPrefersDark(): boolean {
  return window.matchMedia('(prefers-color-scheme: dark)').matches
}

function applyThemeClass(dark: boolean) {
  if (dark) {
    document.documentElement.classList.add('dark')
  } else {
    document.documentElement.classList.remove('dark')
  }
}

export const useThemeStore = defineStore('theme', () => {
  const mode = ref<ThemeMode>((localStorage.getItem(STORAGE_KEY) as ThemeMode) || 'dark')

  const isDark = computed(() => {
    if (mode.value === 'system') return getSystemPrefersDark()
    return mode.value === 'dark'
  })

  function setMode(m: ThemeMode) {
    mode.value = m
    localStorage.setItem(STORAGE_KEY, m)
  }

  function toggle() {
    setMode(isDark.value ? 'light' : 'dark')
  }

  // Apply theme class whenever isDark changes
  watch(isDark, (dark) => applyThemeClass(dark), { immediate: true })

  // Listen for system theme changes when mode is 'system'
  const mql = window.matchMedia('(prefers-color-scheme: dark)')
  mql.addEventListener('change', () => {
    if (mode.value === 'system') {
      applyThemeClass(getSystemPrefersDark())
    }
  })

  return { mode, isDark, setMode, toggle }
})
