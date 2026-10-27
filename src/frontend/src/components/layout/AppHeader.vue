<template>
  <header class="app-header">
    <button
      type="button"
      class="icon-btn menu-btn no-drag"
      :aria-label="t('header.openNavigation')"
      @click="emit('open-menu')"
    >
      <el-icon :size="16"><Menu /></el-icon>
    </button>

    <!-- Looks like a search field, opens the command palette (Ctrl K):
         sites, services, pages and actions in one place. -->
    <button type="button" class="search no-drag" @click="requestCommandPalette()">
      <el-icon :size="14" aria-hidden="true"><Search /></el-icon>
      <span class="search-text">{{ t('header.searchPlaceholder') }}</span>
      <kbd>{{ shortcutLabel }}</kbd>
    </button>

    <div class="header-right no-drag">
      <button
        v-if="updatesStore.hasUpdate"
        type="button"
        class="update-badge"
        :title="t('header.newVersionAvailable', { version: updatesStore.latestVersion })"
        @click="openUpdateTab"
      >
        <span class="update-dot" aria-hidden="true" />
        <span class="update-label mono">v{{ updatesStore.latestVersion }}</span>
      </button>

      <span v-if="!daemonStore.connected" class="conn-pill conn-err" role="status">
        <span class="conn-dot" aria-hidden="true" />{{ t('header.offline') }}
      </span>

      <!-- Interface density: Simple hides advanced destinations and
           columns, Advanced shows everything. One click, both visible. -->
      <div class="tray" role="group" :aria-label="t('settings.mode.label')" :title="t('settings.mode.description')">
        <button
          type="button"
          class="tray-key"
          :aria-pressed="uiModeStore.isSimple"
          @click="uiModeStore.setUiMode('simple')"
        >{{ t('settings.mode.simple') }}</button>
        <button
          type="button"
          class="tray-key"
          :aria-pressed="uiModeStore.isAdvanced"
          @click="uiModeStore.setUiMode('advanced')"
        >{{ t('settings.mode.advanced') }}</button>
      </div>

      <el-dropdown trigger="click" @command="onLocaleChange">
        <button type="button" class="icon-btn lang-btn mono" aria-haspopup="menu" :aria-label="t('settings.language')" :title="t('settings.language')">
          {{ currentLocale.toUpperCase() }}
          <el-icon :size="12" aria-hidden="true"><ArrowDown /></el-icon>
        </button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="en" :disabled="currentLocale === 'en'">
              {{ t('settings.languageEn') }}
            </el-dropdown-item>
            <el-dropdown-item command="cs" :disabled="currentLocale === 'cs'">
              {{ t('settings.languageCs') }}
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>

      <button
        type="button"
        class="icon-btn"
        :aria-label="isDark ? t('header.lightMode') : t('header.darkMode')"
        :title="isDark ? t('header.lightMode') : t('header.darkMode')"
        @click="toggleTheme"
      >
        <el-icon :size="16"><Sunny v-if="isDark" /><Moon v-else /></el-icon>
      </button>
    </div>
  </header>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Moon, Sunny, Search, Menu, ArrowDown } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useThemeStore } from '../../stores/theme'
import { useUiModeStore } from '../../stores/uiMode'
import { useUpdatesStore } from '../../stores/updates'
import { setLocale, type Locale } from '../../i18n'
import { requestCommandPalette } from '../../utils/commandPalette'

const emit = defineEmits<{ 'open-menu': [] }>()

const router = useRouter()
const daemonStore = useDaemonStore()
const themeStore = useThemeStore()
const uiModeStore = useUiModeStore()
const updatesStore = useUpdatesStore()
const { t, locale } = useI18n()

const shortcutLabel = navigator.platform.toLowerCase().includes('mac') ? '⌘ K' : 'Ctrl K'

function openUpdateTab() {
  void router.push({ path: '/settings', query: { tab: 'update' } })
}
const isDark = computed(() => themeStore.isDark)
const currentLocale = computed(() => String(locale.value))
function toggleTheme() { themeStore.toggle() }
function onLocaleChange(next: Locale) { setLocale(next) }
</script>

<style scoped>
/* Top bar: search on the left, density / language / theme on the right.
   Draggable as a window title area; every control opts out. */
.app-header {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
  height: 52px;
  padding: 0 var(--wdc-space-5);
  background: var(--wdc-bg);
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
  -webkit-app-region: drag;
}
.no-drag { -webkit-app-region: no-drag; }

.icon-btn {
  display: inline-grid;
  place-items: center;
  min-width: 32px;
  height: 32px;
  padding: 0 var(--wdc-space-1-5);
  border: 1px solid transparent;
  border-radius: var(--wdc-radius);
  background: transparent;
  color: var(--wdc-text-2);
  font: inherit;
  cursor: pointer;
  -webkit-app-region: no-drag;
  transition: background-color var(--wdc-dur-micro) var(--wdc-ease-out), color var(--wdc-dur-micro) var(--wdc-ease-out);
}
@media (hover: hover) {
  .icon-btn:hover { background: var(--wdc-surface-2); color: var(--wdc-text); }
}
.icon-btn:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; }
.lang-btn {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1);
  font-size: 0.78rem;
}
.menu-btn { display: none; }

.search {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
  flex: 0 1 400px;
  min-width: 0;
  height: 34px;
  padding: 0 var(--wdc-space-2) 0 var(--wdc-space-3);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface);
  color: var(--wdc-text-3);
  font: inherit;
  font-size: 0.87rem;
  text-align: left;
  cursor: text;
  transition: border-color var(--wdc-dur-micro) var(--wdc-ease-out);
}
@media (hover: hover) {
  .search:hover { border-color: var(--wdc-text-3); }
}
.search:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 1px; }
.search-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
kbd {
  font-family: var(--wdc-font-mono);
  font-size: 0.72rem;
  padding: var(--wdc-space-0-5) var(--wdc-space-1-5);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius-xs);
  background: var(--wdc-key-face);
  color: var(--wdc-text-2);
}

.header-right {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
}

/* Segmented tray: the pressed key sits sunken in the track. */
.tray {
  display: inline-flex;
  gap: var(--wdc-space-0-5);
  padding: var(--wdc-space-0-5);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-surface-2);
}
.tray-key {
  min-height: 28px;
  padding: 0 var(--wdc-space-3);
  border: 1px solid transparent;
  border-radius: var(--wdc-radius);
  background: transparent;
  color: var(--wdc-text-2);
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.82rem;
  cursor: pointer;
  transition: background-color var(--wdc-dur-micro) var(--wdc-ease-out), color var(--wdc-dur-micro) var(--wdc-ease-out);
}
@media (hover: hover) {
  .tray-key:hover { color: var(--wdc-text); }
}
/* Same accent wash as the active menu row, so the chosen mode is obvious. */
.tray-key[aria-pressed="true"] {
  background: var(--wdc-accent-dim);
  border-color: color-mix(in oklab, var(--wdc-accent) 55%, transparent);
  color: var(--wdc-accent-ink);
  font-weight: 600;
}
.tray-key:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 1px; }

.conn-pill {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  font-size: 0.82rem;
  font-weight: 500;
}
.conn-err { color: var(--wdc-status-error); }
.conn-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: currentColor;
}

.update-badge {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  min-height: 28px;
  padding: 0 var(--wdc-space-2-5);
  border: 1px solid color-mix(in oklab, var(--wdc-status-starting) 45%, transparent);
  border-radius: var(--wdc-radius);
  background: color-mix(in oklab, var(--wdc-status-starting) 12%, transparent);
  color: var(--wdc-status-starting);
  font-size: 0.78rem;
  cursor: pointer;
}
@media (hover: hover) {
  .update-badge:hover { background: var(--wdc-status-starting); color: var(--wdc-badge-fg); }
}
.update-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
}

@media (max-width: 959px) {
  .app-header { padding: 0 var(--wdc-space-3); gap: var(--wdc-space-2); }
  .menu-btn { display: inline-grid; }
  kbd { display: none; }
}
@media (max-width: 640px) {
  .tray { display: none; }
}
</style>
