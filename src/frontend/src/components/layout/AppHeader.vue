<template>
  <header class="app-header" style="-webkit-app-region: drag;">
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
      <!-- F96: self-updater badge. Surfaces when a newer release is
           tagged on nks-hub/webdev-console; clicking routes to
           Settings → Update tab where the user can download / install. -->
      <button
        v-if="updatesStore.hasUpdate"
        class="update-badge"
        :title="`Nová verze v${updatesStore.latestVersion} je dostupná`"
        @click="openUpdateTab"
      >
        <span class="update-dot" />
        <span class="update-label">v{{ updatesStore.latestVersion }}</span>
      </button>

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
import { useUiModeStore } from '../../stores/uiMode'
import { useUpdatesStore } from '../../stores/updates'
import { usePluginsStore } from '../../stores/plugins'
import { setLocale, type Locale } from '../../i18n'

const router = useRouter()
const route = useRoute()
const daemonStore = useDaemonStore()
const themeStore = useThemeStore()
const uiMode = useUiModeStore()
const updatesStore = useUpdatesStore()
const pluginsStore = usePluginsStore()
const { t, locale } = useI18n()

// F96: navigate to the Settings → Update tab when the header badge is clicked.
function openUpdateTab() {
  void router.push({ path: '/settings', query: { tab: 'update' } })
}
const isDark = computed(() => themeStore.isDark)
const currentLocale = computed(() => String(locale.value))
const currentTitle = computed(() => {
  const key = route.meta?.titleKey as string | undefined
  if (key) return t(key)
  return String(route.meta?.title || 'Control Surface')
})
function toggleTheme() { themeStore.toggle() }
function onLocaleChange(next: Locale) { setLocale(next) }

// PHP dropped from top-level nav — it's accessible via the Services dashboard
// (toggle + config editor) and via its plugin panel at /plugin/nks.wdc.php.
// Keeping runtime-specific managers out of the top nav prevents the menu from
// exploding as we add Node/Go/Python/etc.
// F91: /ssl is plugin-owned (nks.wdc.ssl). If the SSL plugin is disabled the
// tab disappears — the shared pluginsStore.isRouteVisible check hides any
// plugin-owned route whose plugin is currently off, so we never render a
// broken nav link. Non-plugin core routes (/dashboard, /sites, /databases,
// /settings) always bypass the check.
const allNavItems = [
  { path: '/dashboard', label: () => t('nav.services'), requiresAdvanced: true },
  { path: '/sites', label: () => t('nav.sites'), requiresAdvanced: false },
  { path: '/databases', label: () => t('nav.databases'), requiresAdvanced: true },
  { path: '/plugins/mysql', label: () => 'MySQL', requiresAdvanced: true, pluginId: 'nks.wdc.mysql' },
  { path: '/plugins/postgresql', label: () => 'PostgreSQL', requiresAdvanced: true, pluginId: 'nks.wdc.postgresql' },
  { path: '/ssl', label: () => t('nav.ssl'), requiresAdvanced: true },
  { path: '/settings', label: () => t('nav.settings'), requiresAdvanced: false },
]
const navItems = computed(() =>
  allNavItems
    .filter(i => !i.requiresAdvanced || uiMode.isAdvanced)
    .filter(i => !('pluginId' in i) || pluginsStore.manifests.some(p => p.id === i.pluginId && p.enabled))
    .filter(i => pluginsStore.isRouteVisible(i.path))
    .map(i => ({ path: i.path, label: i.label() }))
)

function isActive(path: string) {
  return route.path === path || route.path.startsWith(path + '/')
}
</script>

<style scoped>
/*
  Header sits one elevation step above sidebar/content so it visually
  detaches as a distinct top bar. Strong bottom border seals the
  boundary against the page area below; subtle inset highlight at the
  top adds depth. No more loud rgba 0.08 hairline (which used to be
  the only thing separating header from content).
*/
.app-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 52px;
  padding: 0 20px;
  background: var(--wdc-elevated);
  border-bottom: 1px solid var(--wdc-border-strong);
  flex-shrink: 0;
  gap: 16px;
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04), 0 1px 2px rgba(0, 0, 0, 0.20);
}
html:not(.dark) .app-header {
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.6), 0 1px 2px rgba(15, 23, 42, 0.06);
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
  border-radius: 4px;
  /* Flat: solid fill, no gradient, no drop shadow */
  background: var(--wdc-accent);
  color: var(--wdc-bg);
  font-size: 0.74rem;
  font-weight: 800;
  letter-spacing: 0.08em;
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
  font-size: 0.72rem;
  font-weight: 600;
  letter-spacing: 0.04em;
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
  font-size: 0.82rem;
  font-weight: 600;
  color: var(--wdc-text-3);
  text-decoration: none;
  border-radius: 999px;
  transition: color 0.12s, background 0.12s, box-shadow 0.12s;
  white-space: nowrap;
}

.nav-tab:hover {
  color: var(--wdc-text);
  background: var(--wdc-hover);
}

/*
  Active tab now has a solid accent-dim chip + accent text so it
  reads as the obvious "current page" without the previous gradient
  trick (which was barely visible against the surface bg).
*/
.nav-tab.active {
  color: var(--wdc-accent);
  background: var(--wdc-accent-dim);
  box-shadow: inset 0 0 0 1px rgba(86, 194, 255, 0.30);
}
html:not(.dark) .nav-tab.active {
  box-shadow: inset 0 0 0 1px rgba(0, 109, 125, 0.30);
}

.header-right {
  display: flex;
  align-items: center;
  gap: 10px;
}

.header-right :deep(.el-button) {
  min-width: 36px !important;
  height: 36px !important;
}

.header-right :deep(.el-button.is-circle) {
  width: 36px !important;
  min-width: 36px !important;
  height: 36px !important;
}

.lang-btn {
  min-width: 44px !important;
  padding: 0 12px !important;
}

.conn-pill {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border-radius: 20px;
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  border: 1px solid;
  min-height: 32px;
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

/* F96 update badge — amber accent so it reads as distinct from the
   green/red connection pill. Hides when hasUpdate is false. */
.update-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 0 12px;
  min-height: 32px;
  border-radius: 999px;
  border: 1px solid var(--wdc-warning);
  background: var(--wdc-warning);
  color: #141006;
  font-size: 0.78rem;
  font-weight: 600;
  font-family: 'JetBrains Mono', monospace;
  cursor: pointer;
  transition: background 0.2s ease;
}
.update-badge:hover { filter: brightness(1.05); }
.update-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
  animation: glow 2.2s ease-in-out infinite;
}
.update-label { letter-spacing: 0.02em; }
.update-badge .update-label { color: #141006; }
:global(html:not(.dark)) .update-badge {
  background: #704d0c;
  border-color: #704d0c;
  color: #ffffff !important;
}
:global(html:not(.dark)) .update-badge .update-label { color: #ffffff !important; }

@media (max-width: 760px) {
  .app-header {
    padding: 0 8px;
    gap: 8px;
  }

  .logo-copy,
  .header-nav,
  .conn-pill {
    display: none;
  }

  .header-nav {
    justify-content: flex-start;
    overflow-x: auto;
    scrollbar-width: none;
  }

  .header-nav::-webkit-scrollbar {
    display: none;
  }

  .nav-tab {
    padding: 7px 10px;
    font-size: 0.78rem;
  }

  .header-right {
    gap: 6px;
  }

  .update-badge {
    min-width: 40px;
    padding: 0 8px;
  }
}

</style>
