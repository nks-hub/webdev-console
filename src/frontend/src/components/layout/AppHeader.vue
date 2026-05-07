<template>
  <header class="app-header" style="-webkit-app-region: drag;">
    <!--
      Top nav dropped — all routes already exist in AppSidebar
      (Dashboard, Sites, Databases, MySQL, PostgreSQL, SSL, Settings).
      Header is now just brand + page title context, freeing space and
      removing dual-nav cognitive load.
    -->
    <div class="header-left" style="-webkit-app-region: no-drag">
      <span class="app-logo" @click="router.push('/dashboard')">
        <span class="logo-mark">NW</span>
        <span class="logo-text">NKS WDC</span>
      </span>
      <span class="page-context" v-if="currentTitle">{{ currentTitle }}</span>
    </div>

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
// Top nav dropped in SOFT VIBRANT — sidebar owns all routing now.
</script>

<style scoped>
/*
  ─── Header — SOFT VIBRANT shell ──────────────────────────────────
  Slim 52px bar, surface bg, soft 1px border-bottom. Logo + page
  context on the left, action chips on the right. No top nav (it
  duplicated the sidebar).
*/
.app-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 52px;
  padding: 0 20px;
  background: var(--wdc-surface);
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
  gap: 24px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 16px;
}

.page-context {
  font-size: 0.78rem;
  font-weight: 500;
  color: var(--wdc-text-3);
  text-transform: uppercase;
  letter-spacing: 0.08em;
  padding-left: 16px;
  border-left: 1px solid var(--wdc-border);
}

.app-logo {
  display: flex;
  align-items: center;
  gap: 10px;
  cursor: pointer;
  user-select: none;
}

.logo-mark {
  width: 28px;
  height: 28px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
  background: var(--wdc-accent);
  color: #ffffff;
  font-size: 0.72rem;
  font-weight: 800;
  letter-spacing: 0.06em;
}

/* Logo text inline next to mark. */
.logo-text {
  font-size: 0.86rem;
  font-weight: 700;
  letter-spacing: 0.02em;
  color: var(--wdc-text);
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

/*
  Connection pill — minimal: just a colored dot + small label, no
  fill, no border. Was a chunky filled pill that doubled as background
  noise.
*/
.conn-pill {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 6px;
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  background: transparent;
  border: none;
  color: var(--wdc-text-3);
}

.conn-ok { color: var(--wdc-status-running); }
.conn-err { color: var(--wdc-status-error); }

/*
  Static dots — no opacity animation, no box-shadow glow. Both used
  to pulse together (different periods produced an apparent fast
  flicker), and the box-shadow forced a GPU repaint each frame even
  when the page was idle.
*/
.conn-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: currentColor;
}

/*
  Update badge — solid amber pill so a fresh release pops in the
  header. Mono label keeps the version readable.
*/
.update-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 0 12px;
  min-height: 32px;
  border-radius: 999px;
  border: 1px solid var(--wdc-status-starting);
  background: rgba(245, 158, 11, 0.16);
  color: var(--wdc-status-starting);
  font-size: 0.78rem;
  font-weight: 700;
  font-family: 'JetBrains Mono', monospace;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s, color 0.15s;
}
.update-badge:hover {
  background: var(--wdc-status-starting);
  color: #141006;
}
html:not(.dark) .update-badge:hover { color: #ffffff; }
/* Static dot — was pulsing at 2.2s, paired with conn-dot's 2s pulse,
   the offset cycles read as a fast flicker. */
.update-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
}
.update-label { letter-spacing: 0.02em; }

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
