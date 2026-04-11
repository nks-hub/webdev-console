<template>
  <header class="app-header" :style="{ WebkitAppRegion: 'drag' } as any">
    <div class="header-left" style="-webkit-app-region: no-drag">
      <span class="app-logo" @click="router.push('/sites')">
        <span class="logo-mark">NW</span>
        <span class="logo-copy">
          <span class="logo-text">NKS WDC</span>
          <span class="logo-sub">{{ currentTitle }}</span>
        </span>
      </span>
    </div>

    <nav class="header-nav" style="-webkit-app-region: no-drag">
      <router-link
        v-for="item in navItems"
        :key="item.path"
        :to="item.path"
        class="nav-tab"
        :class="{ active: isActive(item.path) }"
      >
        {{ item.label }}
      </router-link>
    </nav>

    <div class="header-right" style="-webkit-app-region: no-drag">
      <div class="conn-pill" :class="daemonStore.connected ? 'conn-ok' : 'conn-err'">
        <span class="conn-dot" />
        {{ daemonStore.connected ? t('header.connected') : t('header.offline') }}
      </div>

      <el-dropdown trigger="click" @command="onLocaleChange">
        <el-button size="small" text class="lang-btn" :title="t('settings.language')">
          {{ currentLocale.toUpperCase() }}
        </el-button>
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

      <el-button circle size="small" @click="toggleTheme" :title="isDark ? t('header.lightMode') : t('header.darkMode')">
        <el-icon><Moon v-if="isDark" /><Sunny v-else /></el-icon>
      </el-button>
    </div>
  </header>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Moon, Sunny } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useThemeStore } from '../../stores/theme'
import { setLocale, type Locale } from '../../i18n'

const router = useRouter()
const route = useRoute()
const daemonStore = useDaemonStore()
const themeStore = useThemeStore()
const { t, locale } = useI18n()
const isDark = computed(() => themeStore.isDark)
const currentLocale = computed(() => String(locale.value))
const currentTitle = computed(() => String(route.meta?.title || 'Control Surface'))
function toggleTheme() { themeStore.toggle() }
function onLocaleChange(next: Locale) { setLocale(next) }

const navItems = computed(() => [
  { path: '/dashboard', label: t('nav.services') },
  { path: '/sites', label: t('nav.sites') },
  { path: '/databases', label: t('nav.databases') },
  { path: '/ssl', label: t('nav.ssl') },
  { path: '/php', label: t('nav.php') },
  { path: '/settings', label: t('nav.settings') },
])

function isActive(path: string) {
  return route.path === path || route.path.startsWith(path + '/')
}
</script>

<style scoped>
.app-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 48px;
  padding: 0 18px;
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.03), transparent),
    var(--wdc-surface);
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  flex-shrink: 0;
  gap: 16px;
}

.app-logo {
  display: flex;
  align-items: center;
  gap: 10px;
  cursor: pointer;
  user-select: none;
}

.logo-mark {
  width: 30px;
  height: 30px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 10px;
  background: linear-gradient(135deg, var(--wdc-accent), var(--wdc-accent-2));
  color: #081017;
  font-size: 0.74rem;
  font-weight: 800;
  letter-spacing: 0.08em;
  box-shadow: 0 10px 18px rgba(86, 194, 255, 0.18);
}

.logo-copy {
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.logo-text {
  font-size: 0.84rem;
  font-weight: 800;
  letter-spacing: 0.08em;
  color: var(--wdc-text);
}

.logo-sub {
  font-size: 0.68rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--wdc-text-3);
}

/* Header nav tabs */
.header-nav {
  display: flex;
  align-items: center;
  gap: 2px;
  flex: 1;
  justify-content: center;
}

.nav-tab {
  padding: 7px 14px;
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--wdc-text-2);
  text-decoration: none;
  border-radius: 999px;
  transition: all 0.12s;
  white-space: nowrap;
}

.nav-tab:hover {
  color: var(--wdc-text);
  background: var(--wdc-hover);
}

.nav-tab.active {
  color: var(--wdc-text);
  background: linear-gradient(180deg, rgba(86, 194, 255, 0.16), rgba(86, 194, 255, 0.08));
  font-weight: 600;
}

.header-right {
  display: flex;
  align-items: center;
  gap: 10px;
}

.conn-pill {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border-radius: 20px;
  font-size: 0.68rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  border: 1px solid;
}

.conn-ok {
  color: var(--wdc-status-running);
  border-color: rgba(34, 197, 94, 0.3);
  background: rgba(34, 197, 94, 0.06);
}

.conn-err {
  color: var(--wdc-status-error);
  border-color: rgba(239, 68, 68, 0.3);
  background: rgba(239, 68, 68, 0.06);
}

.conn-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
}

.conn-ok .conn-dot {
  animation: glow 2s ease-in-out infinite;
}

@keyframes glow {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}
</style>
