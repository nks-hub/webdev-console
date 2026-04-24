<template>
  <div class="settings-page">
    <div class="page-header">
      <div>
        <h1 class="page-title">{{ $t('settings.title') }}</h1>
        <p class="page-subtitle">{{ $t('settings.subtitle') }}</p>
      </div>
    </div>

    <div class="page-body">
      <el-tabs v-model="activeTab" class="settings-tabs">
        <!-- Ports tab -->
        <el-tab-pane v-if="uiModeStore.isAdvanced" :label="$t('settings.tabs.ports')" name="ports">
          <div class="tab-content">
            <p class="tab-desc">{{ $t('settings.ports.description') }}</p>

            <!-- Task 15: plugin-owned ports. Pulled from GET /api/plugins/ports
                 (IPortMetadata DI registrations per task 25). Only active
                 plugins show up — inactive ones are hidden so the user
                 doesn't see rows for services that aren't running. -->
            <div v-if="pluginPorts.length > 0" class="settings-card" style="margin-bottom: 16px">
              <header class="settings-card-header">
                <span class="settings-card-title">Plugin ports</span>
                <span style="font-size: 0.72rem; color: var(--wdc-text-3)">{{ pluginPorts.length }} active</span>
              </header>
              <div class="settings-card-body">
                <el-form label-position="left" label-width="200px" size="small" style="max-width: 480px">
                  <el-form-item
                    v-for="p in pluginPorts"
                    :key="p.pluginId + ':' + p.key"
                    :label="p.label"
                  >
                    <el-input-number
                      :model-value="p.currentPort"
                      :min="1"
                      :max="65535"
                      style="width: 100%"
                      disabled
                    />
                    <div class="hint">
                      <code class="mono">{{ p.pluginId }}</code> · default {{ p.defaultPort }}
                    </div>
                  </el-form-item>
                </el-form>
              </div>
            </div>

            <!-- Legacy hardcoded ports form — will migrate to IPortMetadata
                 one plugin at a time. For now coexists so users can still
                 edit the values that haven't been wired to plugins yet. -->
            <el-form label-position="left" label-width="160px" size="small" style="max-width: 400px">
              <el-form-item :label="$t('settings.ports.httpPort')">
                <el-input-number v-model="ports.http" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item :label="$t('settings.ports.httpsPort')">
                <el-input-number v-model="ports.https" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item :label="$t('settings.ports.mysqlPort')">
                <el-input-number v-model="ports.mysql" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item :label="$t('settings.ports.redisPort')">
                <el-input-number v-model="ports.redis" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item :label="$t('settings.ports.mailpitSmtp')">
                <el-input-number v-model="ports.mailpitSmtp" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item :label="$t('settings.ports.mailpitHttp')">
                <el-input-number v-model="ports.mailpitHttp" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item :label="$t('settings.ports.phpFpmBase')">
                <el-input-number v-model="phpFpmBasePort" :min="9000" :max="9999" style="width: 100%" />
                <div class="hint">{{ $t('settings.ports.phpFpmFormula') }}</div>
              </el-form-item>
            </el-form>
          </div>
        </el-tab-pane>

        <!-- General tab -->
        <el-tab-pane :label="$t('settings.tabs.general')" name="general">
          <div class="tab-content">
            <p class="tab-desc">Application behavior and startup preferences.</p>
            <el-form label-position="left" label-width="180px" size="small" style="max-width: 500px">
              <el-form-item label="Language">
                <el-select
                  :model-value="locale"
                  @update:model-value="onLocaleChange"
                  style="width: 160px"
                >
                  <el-option label="English" value="en" />
                  <el-option label="Čeština" value="cs" />
                </el-select>
              </el-form-item>
              <el-form-item :label="$t('settings.theme.label')">
                <el-radio-group
                  :model-value="themeStore.mode"
                  @update:model-value="themeStore.setMode($event as ThemeMode)"
                >
                  <el-radio-button value="dark">{{ $t('settings.theme.dark') }}</el-radio-button>
                  <el-radio-button value="light">{{ $t('settings.theme.light') }}</el-radio-button>
                  <el-radio-button value="system">{{ $t('settings.theme.system') }}</el-radio-button>
                </el-radio-group>
              </el-form-item>
              <el-form-item :label="$t('settings.mode.label')">
                <el-switch
                  :model-value="uiModeStore.isAdvanced"
                  :active-text="$t('settings.mode.advanced')"
                  :inactive-text="$t('settings.mode.simple')"
                  @change="(val: boolean) => uiModeStore.setUiMode(val ? 'advanced' : 'simple')"
                />
                <div class="hint">{{ $t('settings.mode.description') }}</div>
              </el-form-item>
              <el-form-item :label="$t('settings.general.runOnStartup')">
                <el-switch v-model="runOnStartup" />
              </el-form-item>
              <!-- Auto-start je per-plugin nastavení — najdeš ho na kartě
                   jednotlivého pluginu v Plugin Manageru. Žádné duplicitní
                   ovládání tady, aby nebyl zmatek „kde to vlastně zapnu“. -->
              <el-form-item :label="$t('settings.general.defaultPhpVersion')">
                <el-select v-model="defaultPhp" style="width: 160px" placeholder="Select">
                  <el-option v-for="v in phpVersions" :key="v" :label="'PHP ' + v" :value="v" />
                </el-select>
              </el-form-item>
              <el-form-item label="DNS Cache">
                <el-button size="small" @click="flushDns" :loading="flushingDns">
                  Flush DNS Cache
                </el-button>
              </el-form-item>
              <!-- F73: Migrate MAMP moved here from Sites toolbar. It's a
                   one-off import operation that belongs in settings, not in
                   the day-to-day Sites page where the toolbar button was
                   cluttering the primary site workflow. -->
              <el-form-item label="MAMP PRO import">
                <el-button size="small" @click="discoverMamp" :loading="mampDiscovering" title="Import sites from MAMP PRO">
                  Migrate MAMP
                </el-button>
                <div class="hint">Scan for MAMP PRO installation on this machine and import its vhosts as WDC sites.</div>
              </el-form-item>

              <el-divider />

              <el-form-item label="Telemetry">
                <el-switch v-model="telemetryEnabled" />
                <div class="hint">
                  Send anonymous usage statistics to help improve NKS WDC.
                  No personal data, no site names, no code — just feature usage counts.
                </div>
              </el-form-item>
              <el-form-item label="Crash reports" v-if="telemetryEnabled">
                <el-switch v-model="telemetryCrashReports" />
                <div class="hint">
                  Send crash stack traces via Sentry when a daemon exception occurs.
                  Disabled when telemetry is off.
                </div>
              </el-form-item>
            </el-form>
          </div>
        </el-tab-pane>

        <!-- Paths tab -->
        <el-tab-pane v-if="uiModeStore.isAdvanced" :label="$t('settings.tabs.paths')" name="paths">
          <div class="tab-content">
            <p class="tab-desc">Override binary paths. Leave blank to use auto-detected defaults.</p>
            <!-- F79: Browse buttons open the native file/folder dialog via
                 electronAPI.showOpenDialog. Falls back to manual typing when
                 running outside Electron (dev browser, etc.). -->
            <el-form label-position="top" size="small" style="max-width: 560px">
              <el-form-item label="Apache httpd.exe">
                <el-input v-model="paths.apache" placeholder="C:\nks-wdc\binaries\apache\2.4\bin\httpd.exe">
                  <template #append>
                    <el-button @click="browsePath('apache', 'file')">Browse</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item label="MySQL mysqld.exe">
                <el-input v-model="paths.mysql" placeholder="C:\nks-wdc\binaries\mysql\8.0\bin\mysqld.exe">
                  <template #append>
                    <el-button @click="browsePath('mysql', 'file')">Browse</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item label="PHP executable">
                <el-input v-model="paths.php" placeholder="C:\nks-wdc\binaries\php\8.4\php.exe">
                  <template #append>
                    <el-button @click="browsePath('php', 'file')">Browse</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item label="Redis redis-server.exe">
                <el-input v-model="paths.redis" placeholder="C:\nks-wdc\binaries\redis\7.2\redis-server.exe">
                  <template #append>
                    <el-button @click="browsePath('redis', 'file')">Browse</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item label="Sites config directory">
                <el-input v-model="paths.sitesDir" placeholder="C:\nks-wdc\conf\vhosts">
                  <template #append>
                    <el-button @click="browsePath('sitesDir', 'folder')">Browse</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item label="Hosts file">
                <el-input v-model="paths.hostsFile" placeholder="C:\Windows\System32\drivers\etc\hosts">
                  <template #append>
                    <el-button @click="browsePath('hostsFile', 'file')">Browse</el-button>
                  </template>
                </el-input>
                <div class="hint">
                  Path to the system hosts file for local domain resolution.
                  Leave blank for the OS default.
                </div>
              </el-form-item>

              <el-divider />

              <el-form-item label="Data directory">
                <el-input
                  :model-value="systemInfo?.os?.machine ? `${systemInfo?.daemon?.pid ? '~/.wdc' : '~/.wdc'}` : '~/.wdc'"
                  disabled
                  class="mono-input"
                />
                <div class="hint">
                  Root for all daemon state: sites, binaries, SSL certs, backups, configs.
                  Override with <code>WDC_DATA_DIR</code> environment variable or
                  <code>portable.txt</code> next to the executable.
                </div>
              </el-form-item>
              <el-form-item label="Backup directory">
                <el-input v-model="backupDir" placeholder="~/.wdc/backups" />
              </el-form-item>
              <el-form-item label="Auto-backup interval">
                <el-input-number
                  v-model="backupScheduleHours"
                  :min="0"
                  :max="720"
                  controls-position="right"
                  style="width: 160px"
                />
                <span style="margin-left: 8px; font-size: 0.82rem; color: var(--wdc-text-3)">hours</span>
                <div class="hint">
                  Set to 0 to disable. When &gt; 0, the daemon creates a
                  timestamped backup every N hours and prunes old ones (keeps 10).
                </div>
              </el-form-item>
            </el-form>

            <!-- Manual backup management -->
            <div style="margin-top: 24px; border-top: 1px solid var(--wdc-border); padding-top: 16px">
              <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px">
                <span style="font-weight: 600; font-size: 0.95rem">Backups</span>
                <div style="display: flex; gap: 8px">
                  <el-button size="small" type="primary" @click="manualBackup" :loading="backupCreating">
                    Create Backup
                  </el-button>
                  <el-button size="small" @click="loadBackups" :loading="backupsLoading">
                    {{ $t('common.refresh') }}
                  </el-button>
                </div>
              </div>
              <div v-if="backupsLoading" class="hint">Loading backups...</div>
              <div v-else-if="backupsList.length === 0" class="hint">No backups yet. Click "Create Backup" to create one.</div>
              <el-table v-else :data="backupsList" size="small" stripe style="width: 100%">
                <el-table-column label="Date" width="180">
                  <template #default="{ row }">
                    {{ new Date(row.createdUtc).toLocaleString() }}
                  </template>
                </el-table-column>
                <el-table-column label="Size" width="100">
                  <template #default="{ row }">
                    {{ (row.size / 1024 / 1024).toFixed(1) }} MB
                  </template>
                </el-table-column>
                <el-table-column :label="$t('common.actions')">
                  <template #default="{ row }">
                    <el-button size="small" @click="downloadBackupFile(row.path)">{{ $t('common.download') }}</el-button>
                  </template>
                </el-table-column>
              </el-table>
            </div>
          </div>
        </el-tab-pane>

        <!-- Databases tab -->
        <el-tab-pane v-if="uiModeStore.isAdvanced" :label="$t('settings.tabs.databases')" name="databases">
          <div class="tab-content">
            <p class="tab-desc">MySQL databases managed by NKS WDC.</p>
            <div class="db-list" v-if="databases.length > 0">
              <div class="db-row" v-for="db in databases" :key="db">
                <span class="db-name">{{ db }}</span>
                <el-button size="small" type="danger" text @click="dropDatabase(db)">Drop</el-button>
              </div>
            </div>
            <el-empty v-else description="No user databases" :image-size="48" />
            <div class="db-create">
              <el-input v-model="newDbName" placeholder="new_database" size="small" style="width: 200px" />
              <el-button size="small" type="primary" @click="createDatabase" :disabled="!newDbName">Create</el-button>
            </div>
          </div>
        </el-tab-pane>

        <!-- Advanced tab — integration endpoints -->
        <el-tab-pane v-if="uiModeStore.isAdvanced" :label="$t('settings.tabs.advanced')" name="advanced">
          <div class="tab-content">
            <p class="tab-desc">
              External services the daemon talks to. Leave blank to use built-in defaults.
              Changes take effect after restart or a catalog refresh.
            </p>
            <el-form label-position="top" size="small" style="max-width: 560px">
              <el-form-item label="Catalog API URL">
                <el-input
                  v-model="catalogUrl"
                  placeholder="https://wdc.nks-hub.cz"
                  class="mono-input"
                >
                  <template #append>
                    <el-button :loading="refreshingCatalog" @click="refreshCatalog">
                      {{ $t('common.refresh') }}
                    </el-button>
                  </template>
                </el-input>
                <div class="hint">
                  URL of the NKS WDC catalog-api service (see
                  <code>services/catalog-api/</code>). Electron auto-spawns
                  a local sidecar on <code>127.0.0.1:8765</code> in dev mode.
                  Point at your self-hosted deployment for team-wide binary
                  versions or leave blank for the default.
                </div>
                <div class="hint" v-if="catalogStatus">
                  <span :class="['status-dot', catalogStatus.ok ? 'ok' : 'err']"></span>
                  {{ catalogStatus.message }}
                </div>
              </el-form-item>
              <el-form-item label="Binary releases">
                <el-button size="small" @click="testCatalogReachable" :loading="testingCatalog">
                  Test connection
                </el-button>
                <el-button
                  size="small"
                  type="primary"
                  @click="openCatalogAdmin"
                  :disabled="!catalogUrl"
                >
                  {{ $t('common.open') }} admin UI
                </el-button>
              </el-form-item>
              <el-form-item label="MySQL root password">
                <div class="mysql-root-row">
                  <el-input
                    v-model="mysqlRootPassword"
                    type="password"
                    show-password
                    :placeholder="mysqlRootExists ? '•••••••• (stored)' : 'Enter root password to sync with mysqld'"
                    class="mono-input"
                  />
                  <el-button
                    size="small"
                    :loading="mysqlRootSaving"
                    :disabled="!mysqlRootPassword"
                    @click="saveMysqlRootPassword"
                  >Save</el-button>
                </div>
                <div class="hint">
                  {{ mysqlRootExists ? 'A password is currently stored (encrypted via DPAPI).' : 'No password stored — WDC cannot authenticate to mysqld.' }}
                  Use this field when your mysqld root password was set outside WDC (external MySQL install, MAMP import, or manual change) — WDC only syncs its stored copy, you still need to run
                  <code>ALTER USER 'root'@'localhost' IDENTIFIED BY '…'</code>
                  on the server itself.
                </div>
              </el-form-item>
              <el-form-item label="Plugin auto-sync">
                <el-switch v-model="pluginAutoSync" />
                <el-button
                  size="small"
                  style="margin-left: 12px"
                  :loading="syncingPlugins"
                  @click="syncPluginsNow"
                >Sync now</el-button>
                <div class="hint">
                  When enabled the daemon pulls the plugin catalog from the
                  URL above on startup + every 6 hours and downloads any
                  missing plugin releases into
                  <code>~/.wdc/plugins/&lt;id&gt;/&lt;version&gt;/</code>.
                  Leave off for dev builds that use the bundled
                  <code>build/plugins/</code>. Env var
                  <code>NKS_WDC_PLUGIN_AUTOSYNC=1</code> still wins when set.
                </div>
                <div v-if="pluginSyncStatus" class="hint">
                  <span :class="['status-dot', pluginSyncStatus.ok ? 'ok' : 'err']"></span>
                  {{ pluginSyncStatus.message }}
                </div>
                <div v-if="pluginCatalogStatus" class="hint">
                  Catalog: {{ pluginCatalogStatus.catalogCount }} plugin{{ pluginCatalogStatus.catalogCount === 1 ? '' : 's' }} ·
                  cached: {{ pluginCatalogStatus.cachedCount }} ·
                  last sync: {{ pluginCatalogStatus.lastFetch ? formatAgo(pluginCatalogStatus.lastFetch) : 'never' }}
                </div>
              </el-form-item>
            </el-form>

            <!-- Danger zone — destructive reset operations. Kept at the
                 bottom so accidental scroll-past doesn't hit it first, and
                 every button requires an explicit confirm before doing
                 anything. Scope tiers:
                 • Settings reset → wipes only the `settings` table (ports,
                   paths, catalog URL, autostart flags, sync tokens). Sites,
                   databases, installed binaries, plugins, SSL certs are
                   preserved.
                 • Full factory reset → also wipes sites/databases via
                   manager delete calls. Does NOT touch ~/.wdc/binaries so a
                   full re-download isn't forced.
                 Nuclear option (delete ~/.wdc entirely) stays CLI-only. -->
            <div class="danger-zone">
              <h3 class="danger-title">Danger zone</h3>
              <p class="danger-desc">
                Nevratné operace. WDC se po resetu restartuje a znovu otevře
                uvítacího průvodce. Pokud jsi přihlášený ke katalogu, session
                token se smaže a budeš se muset přihlásit znovu.
              </p>
              <div class="danger-row">
                <div class="danger-info">
                  <strong>Reset nastavení</strong>
                  <span class="hint">
                    Smaže tabulku <code>settings</code> (porty, cesty, catalogUrl,
                    autoStart přepínače, sync.accountToken). Weby, databáze,
                    stažené binárky a pluginy zůstanou.
                  </span>
                </div>
                <el-button
                  type="warning"
                  :loading="resettingSettings"
                  @click="confirmResetSettings"
                >Reset nastavení</el-button>
              </div>
              <div class="danger-row">
                <div class="danger-info">
                  <strong>Kompletní tovární reset</strong>
                  <span class="hint">
                    Reset nastavení + smazání všech webů, databází a
                    pluginových stavů. Binárky pod <code>~/.wdc/binaries/</code>
                    zachovává, aby se nemuselo znovu stahovat Apache/PHP/MySQL.
                  </span>
                </div>
                <el-button
                  type="danger"
                  :loading="resettingFactory"
                  @click="confirmFactoryReset"
                >Tovární reset</el-button>
              </div>
            </div>
          </div>
        </el-tab-pane>

        <!-- Account & Devices tab -->
        <el-tab-pane :label="$t('settings.tabs.account')" name="account">
          <div class="tab-content">
            <!-- F91.4: SSO (catalog-api OIDC) moved from About → Account
                 because signing in belongs with account management, not
                 with "what version is this" metadata. Shown in both
                 simple + advanced modes so simple users can still sign
                 in to their catalog identity. -->
            <section class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">{{ $t('settings.sso.title') }}</span>
                <span v-if="authStore.isAuthenticated" style="font-size: 0.78rem; color: var(--wdc-status-running);">{{ $t('settings.sso.signedIn') }}</span>
              </header>
              <div class="settings-card-body">
                <div v-if="authStore.isAuthenticated" class="sync-actions" style="flex-direction: column; align-items: flex-start; gap: 6px;">
                  <span class="tab-desc" style="margin: 0;">
                    <!-- F91.6: surface SSO identity (email/name/sub from JWT claims). -->
                    {{ authStore.displayName
                        ? $t('settings.sso.signedInAs', { who: authStore.displayName })
                        : $t('settings.sso.signedInAt', { url: $t('settings.sso.configuredCatalog') }) }}
                  </span>
                  <el-button size="small" @click="authStore.logout()">{{ $t('settings.sso.signOut') }}</el-button>
                </div>
                <div v-else class="sync-actions" style="flex-direction: column; align-items: flex-start;">
                  <p class="tab-desc">{{ $t('settings.sso.description') }}</p>
                  <div style="display: flex; gap: 8px; align-items: center;">
                    <el-button
                      size="small"
                      type="primary"
                      :loading="authStore.loginPending"
                      @click="ssoLogin"
                    >{{ $t('settings.sso.signIn') }}</el-button>
                    <span v-if="authStore.loginError" class="sso-error" style="color: var(--wdc-status-error); font-size: 0.78rem;">{{ authStore.loginError }}</span>
                  </div>
                </div>
              </div>
            </section>

            <!-- F91.15: password login restored alongside SSO. The two
                 paths write to the same authStore (token + displayName),
                 just through different entry points — SSO card above
                 opens Authentik, password form here hits
                 /api/v1/auth/login directly. "Unified login" = one
                 Account tab hosting both, not one removed. -->
            <template v-if="uiModeStore.isSimple">
              <section v-if="!accountToken" class="settings-card">
                <header class="settings-card-header">
                  <span class="settings-card-title">{{ $t('settings.tabs.account') }}</span>
                </header>
                <div class="settings-card-body">
                  <p class="tab-desc">{{ $t('settings.account.passwordAlt') }}</p>
                  <el-form label-position="top" size="small" style="max-width: 360px" @submit.prevent="doLogin">
                    <el-form-item label="Email">
                      <el-input v-model="authEmail" placeholder="you@example.com" />
                    </el-form-item>
                    <el-form-item label="Password">
                      <el-input v-model="authPassword" type="password" show-password />
                    </el-form-item>
                    <div class="sync-actions">
                      <el-button type="primary" size="small" :loading="authLoading" @click="doLogin">{{ $t('common.login') }}</el-button>
                      <el-button size="small" :loading="authLoading" @click="doRegister">{{ $t('common.register') }}</el-button>
                    </div>
                    <div class="hint" v-if="authError" style="color: var(--wdc-status-error); margin-top: 8px;">
                      {{ authError }}
                    </div>
                  </el-form>
                </div>
              </section>
              <section v-else class="settings-card">
                <header class="settings-card-header">
                  <span class="settings-card-title">{{ $t('settings.tabs.account') }}</span>
                  <span style="font-size: 0.78rem; color: var(--wdc-text-2);">{{ accountEmail }}</span>
                </header>
                <div class="settings-card-body">
                  <div class="sync-actions">
                    <el-button size="small" type="primary" :loading="syncing" @click="pushToCloud">
                      <el-icon><Upload /></el-icon>
                      <span>Push</span>
                    </el-button>
                    <el-button size="small" :loading="pulling" @click="pullFromCloud">
                      <el-icon><Download /></el-icon>
                      <span>Pull</span>
                    </el-button>
                    <el-button size="small" type="danger" plain @click="doLogout">{{ $t('common.logout') }}</el-button>
                  </div>
                </div>
              </section>
            </template>

            <!-- Advanced mode: full account UI. Shows password form when
                 not signed in, device management + push/pull when signed
                 in. SSO card above is the other entry point; both write
                 the same authStore so switching between them is seamless. -->
            <template v-if="!uiModeStore.isSimple">
              <section v-if="!accountToken" class="settings-card">
                <header class="settings-card-header">
                  <span class="settings-card-title">{{ $t('settings.account.passwordTitle') }}</span>
                </header>
                <div class="settings-card-body">
                  <p class="tab-desc">{{ $t('settings.account.passwordAlt') }}</p>
                  <el-form label-position="top" size="small" style="max-width: 360px" @submit.prevent="doLogin">
                    <el-form-item label="Email">
                      <el-input v-model="authEmail" placeholder="you@example.com" />
                    </el-form-item>
                    <el-form-item label="Password">
                      <el-input v-model="authPassword" type="password" show-password />
                    </el-form-item>
                    <div class="sync-actions">
                      <el-button type="primary" size="small" :loading="authLoading" @click="doLogin">{{ $t('common.login') }}</el-button>
                      <el-button size="small" :loading="authLoading" @click="doRegister">{{ $t('common.register') }}</el-button>
                    </div>
                    <div class="hint" v-if="authError" style="color: var(--wdc-status-error); margin-top: 8px;">
                      {{ authError }}
                    </div>
                  </el-form>
                </div>
              </section>
              <section v-else class="settings-card">
                <header class="settings-card-header">
                  <span class="settings-card-title">Account</span>
                  <span style="font-size: 0.78rem; color: var(--wdc-text-2);">{{ accountEmail }}</span>
                </header>
                <div class="settings-card-body">
                  <div class="sync-actions">
                    <el-button size="small" @click="loadDevicesAccount" :loading="devicesLoading">{{ $t('common.refresh') }} devices</el-button>
                    <el-button size="small" type="danger" plain @click="doLogout">Sign out</el-button>
                  </div>
                </div>
              </section>


              <!-- F91.15: devices list only when signed in — same gate
                   as the Account summary above. -->
              <section v-if="accountToken" class="settings-card">
                <header class="settings-card-header">
                  <span class="settings-card-title">My Devices</span>
                  <span style="font-size: 0.72rem; color: var(--wdc-text-3)">{{ accountDevices.length }} registered</span>
                </header>
                <div class="settings-card-body">
                  <el-table v-if="accountDevices.length > 0" :data="accountDevices" size="small" stripe>
                    <el-table-column label="Name" min-width="180">
                      <template #default="{ row }">
                        <div class="device-name-cell">
                          <el-input
                            v-if="editingDeviceName === row.device_id"
                            v-model="editingDeviceValue"
                            size="small"
                            class="device-name-input"
                            @blur="saveDeviceName(row)"
                            @keydown.enter.prevent="saveDeviceName(row)"
                            @keydown.escape.prevent="editingDeviceName = null"
                          />
                          <span
                            v-else
                            class="device-name-text mono"
                            :style="row.is_current ? 'font-weight: 700' : ''"
                            @dblclick="startEditDeviceName(row)"
                            title="Double-click to rename"
                          >
                            {{ row.name || row.device_id.slice(0, 12) + '…' }}
                          </span>
                          <el-tag v-if="row.is_current" size="small" type="success" effect="dark" style="margin-left: 6px">this</el-tag>
                        </div>
                      </template>
                    </el-table-column>
                    <el-table-column label="OS" width="120">
                      <template #default="{ row }">
                        <span class="mono">{{ (row.os ?? '') + '/' + (row.arch ?? '') }}</span>
                      </template>
                    </el-table-column>
                    <el-table-column label="Sites" width="70" align="center">
                      <template #default="{ row }">{{ row.site_count ?? '—' }}</template>
                    </el-table-column>
                    <el-table-column label="Status" width="90">
                      <template #default="{ row }">
                        <el-tag size="small" :type="row.online ? 'success' : 'info'" effect="dark">
                          {{ row.online ? 'Online' : 'Offline' }}
                        </el-tag>
                      </template>
                    </el-table-column>
                    <el-table-column label="Last sync" width="150">
                      <template #default="{ row }">
                        <span style="font-size: 0.72rem; color: var(--wdc-text-3);">
                          {{ row.last_seen_at ? new Date(row.last_seen_at).toLocaleString() : '—' }}
                        </span>
                      </template>
                    </el-table-column>
                    <el-table-column label="" width="200" align="right">
                      <template #default="{ row }">
                        <div style="display: flex; gap: 6px; justify-content: flex-end">
                          <el-button
                            v-if="!row.is_current"
                            size="small"
                            type="primary"
                            plain
                            @click="pushMyConfigTo(row.device_id)"
                            :loading="pushingTo === row.device_id"
                          >Push here</el-button>
                          <!-- Task 07: unlink flow. Confirm before DELETE
                               since removing a device from the account
                               invalidates its tokens and forces re-login. -->
                          <el-button
                            v-if="!row.is_current"
                            size="small"
                            type="danger"
                            plain
                            @click="unlinkDevice(row)"
                            :loading="unlinkingDevice === row.device_id"
                          >Unlink</el-button>
                        </div>
                      </template>
                    </el-table-column>
                  </el-table>
                  <el-empty v-else description="No devices registered yet. Push settings first." :image-size="48" />
                </div>
              </section>
            </template>
          </div>
        </el-tab-pane>

        <!-- Update tab — visible in both Simple and Advanced -->
        <el-tab-pane :label="$t('settings.tabs.update')" name="update">
          <div class="tab-content">
            <el-descriptions :column="1" border size="small">
              <el-descriptions-item :label="$t('settings.update.current')">
                <span class="mono">v{{ currentVersion }}</span>
              </el-descriptions-item>
              <el-descriptions-item :label="$t('settings.update.latest')">
                <span v-if="updateCheck.loading">{{ $t('common.loading') }}</span>
                <span v-else-if="updateCheck.latest" class="mono">
                  v{{ updateCheck.latest }}
                  <el-tag v-if="updateCheck.hasUpdate" type="warning" size="small" style="margin-left:8px">{{ $t('settings.update.available') }}</el-tag>
                  <el-tag v-else type="success" size="small" style="margin-left:8px">{{ $t('settings.update.upToDate') }}</el-tag>
                </span>
                <span v-else class="text-muted">{{ $t('settings.update.notChecked') }}</span>
              </el-descriptions-item>
              <el-descriptions-item v-if="updateCheck.lastCheckedIso" :label="$t('settings.update.lastChecked')">
                <span class="text-muted">{{ formatRelativeTime(updateCheck.lastCheckedIso) }}</span>
              </el-descriptions-item>
            </el-descriptions>

            <div class="update-actions">
              <el-button size="small" :loading="updateCheck.loading" @click="runUpdateCheck">
                {{ $t('settings.update.check') }}
              </el-button>
              <el-button
                v-if="updateCheck.hasUpdate"
                type="primary"
                size="small"
                :loading="updateCheck.downloading"
                @click="downloadAndInstall"
              >
                {{ $t('settings.update.downloadInstall') }}
              </el-button>
              <el-link
                v-if="updateCheck.downloadUrl"
                :href="updateCheck.downloadUrl"
                target="_blank"
                type="primary"
              >
                {{ $t('settings.update.openRelease') }} →
              </el-link>
            </div>

            <!-- Task 06: download progress bar. Shown while
                 electron-updater is downloading the new bundle (IPC
                 message sets progressPercent). -->
            <div v-if="updateCheck.progressPercent !== null" class="update-progress">
              <el-progress
                :percentage="updateCheck.progressPercent"
                :status="updateCheck.progressPercent >= 100 ? 'success' : undefined"
                :stroke-width="10"
              />
              <div class="update-progress-meta">
                <span>{{ updateCheck.progressPercent >= 100 ? 'Installing…' : 'Downloading…' }}</span>
                <span v-if="updateCheck.progressBytes" class="mono">{{ updateCheck.progressBytes }}</span>
              </div>
            </div>

            <!-- Task 06: changelog markdown — GitHub release body rendered
                 as a conservative subset (headings, lists, inline code).
                 Full release viewable via the "View on GitHub" link. -->
            <section v-if="updateCheck.releaseNotes" class="settings-card" style="margin-top: 12px">
              <header class="settings-card-header">
                <span class="settings-card-title">
                  {{ $t('settings.update.releaseNotesTitle') }} v{{ updateCheck.latest }}
                </span>
                <el-link
                  v-if="updateCheck.releaseUrl"
                  :href="updateCheck.releaseUrl"
                  target="_blank"
                  type="primary"
                  style="font-size: 0.78rem"
                >
                  {{ $t('settings.update.viewOnGithub') }} →
                </el-link>
              </header>
              <!-- eslint-disable-next-line vue/no-v-html — renderReleaseNotes escapes input first -->
              <div class="release-notes settings-card-body" v-html="renderReleaseNotes(updateCheck.releaseNotes)" />
            </section>

            <el-alert
              v-if="updateCheck.error"
              type="error"
              :closable="false"
              style="margin-top: 12px"
            >
              {{ updateCheck.error }}
            </el-alert>
          </div>
        </el-tab-pane>

        <!-- Sync tab — cloud config sync + export/import -->
        <el-tab-pane v-if="uiModeStore.isAdvanced" :label="$t('settings.tabs.sync')" name="sync">
          <div class="tab-content">
            <p class="tab-desc">
              Synchronize your NKS WDC configuration with the cloud catalog
              service, or export/import settings as a file for offline transfer.
            </p>

            <!-- Device identity -->
            <section class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Device</span>
              </header>
              <div class="settings-card-body">
                <el-form label-position="left" label-width="140px" size="small" style="max-width: 500px">
                  <el-form-item label="Device ID">
                    <el-input :model-value="deviceId" disabled class="mono-input">
                      <template #append>
                        <el-button @click="copyDeviceId" title="Copy to clipboard">Copy</el-button>
                      </template>
                    </el-input>
                    <div class="hint">
                      Auto-generated unique identifier for this machine.
                      Used as the key for cloud sync.
                    </div>
                  </el-form-item>
                  <el-form-item label="Device name">
                    <el-input v-model="deviceName" placeholder="e.g. Work Laptop" />
                    <div class="hint">Optional label to identify this device in the sync UI.</div>
                  </el-form-item>
                </el-form>
              </div>
            </section>

            <!-- Cloud sync -->
            <section class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Cloud sync</span>
                <span v-if="syncStatus" :class="['sync-badge', syncStatus.ok ? 'sync-ok' : 'sync-err']">
                  {{ syncStatus.message }}
                </span>
              </header>
              <div class="settings-card-body">
                <p class="tab-desc" style="margin-bottom: 12px;">
                  Push your current settings (sites, services, ports, paths, plugin
                  toggles) to the catalog-api service so a fresh install can
                  restore them with one click.
                </p>
                <div class="sync-actions">
                  <el-button
                    type="primary"
                    size="small"
                    :loading="syncing"
                    :disabled="!catalogUrl && !deviceId"
                    @click="pushToCloud"
                  >
                    <el-icon><Upload /></el-icon>
                    <span>Push to cloud</span>
                  </el-button>
                  <el-button
                    size="small"
                    :loading="pulling"
                    :disabled="!catalogUrl && !deviceId"
                    @click="pullFromCloud"
                  >
                    <el-icon><Download /></el-icon>
                    <span>Pull from cloud</span>
                  </el-button>
                  <el-button
                    size="small"
                    :disabled="!catalogUrl && !deviceId"
                    @click="checkCloudExists"
                    :loading="checkingCloud"
                  >
                    Check status
                  </el-button>
                </div>
                <div class="hint" v-if="lastSyncTime">
                  Last synced: {{ lastSyncDisplay }}
                </div>
              </div>
            </section>

            <!-- Task 03: Cloud snapshots — recent snapshot list from
                 catalog-api /sync/snapshots with restore/delete actions.
                 Snapshots are auto-created by the cloud BEFORE each push
                 overwrites a device config (see catalog-api task 34). -->
            <section v-if="accountToken" class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Snapshots</span>
                <el-button size="small" :loading="snapshotsLoading" @click="loadSnapshots">
                  {{ $t('common.refresh') }}
                </el-button>
              </header>
              <div class="settings-card-body">
                <p class="tab-desc" style="margin-bottom: 10px">
                  Revert to a previous configuration. Cloud keeps the last
                  10 snapshots per device — older ones are pruned automatically.
                </p>
                <el-table v-if="snapshots.length > 0" :data="snapshots" size="small" stripe>
                  <el-table-column label="When" min-width="180">
                    <template #default="{ row }">
                      {{ formatDate(row.created_at) }}
                    </template>
                  </el-table-column>
                  <el-table-column label="Device" min-width="140">
                    <template #default="{ row }">
                      <span class="mono">{{ row.device_id.slice(0, 12) }}…</span>
                    </template>
                  </el-table-column>
                  <el-table-column label="Size" width="100">
                    <template #default="{ row }">
                      <span class="mono">{{ Math.round(row.size_bytes / 1024) }} KB</span>
                    </template>
                  </el-table-column>
                  <el-table-column label="" width="180" align="right">
                    <template #default="{ row }">
                      <div style="display: flex; gap: 6px; justify-content: flex-end">
                        <el-button size="small" plain @click="restoreSnapshot(row)" :loading="snapshotAction === row.id">
                          Restore
                        </el-button>
                        <el-button size="small" type="danger" plain @click="deleteSnapshot(row)" :loading="snapshotAction === row.id">
                          {{ $t('common.delete') }}
                        </el-button>
                      </div>
                    </template>
                  </el-table-column>
                </el-table>
                <el-empty v-else :description="snapshotsLoading ? $t('common.loading') : 'No snapshots yet — push to cloud first'" :image-size="48" />
              </div>
            </section>

            <!-- File export / import -->
            <section class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Export / Import</span>
              </header>
              <div class="settings-card-body">
                <p class="tab-desc" style="margin-bottom: 12px;">
                  Download a JSON file of all settings, or import from a
                  previously exported file. Useful for offline transfers or
                  version-controlled team configs.
                </p>
                <div class="sync-actions">
                  <el-button size="small" @click="exportSettings">
                    <el-icon><Download /></el-icon>
                    <span>Export to file</span>
                  </el-button>
                  <el-button size="small" @click="triggerImport">
                    <el-icon><Upload /></el-icon>
                    <span>Import from file</span>
                  </el-button>
                  <input
                    ref="importFileInput"
                    type="file"
                    accept=".json"
                    style="display: none"
                    @change="importSettings"
                  />
                </div>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- About tab -->
        <el-tab-pane label="About" name="about">
          <div class="tab-content">
            <div class="about-card">
              <div class="about-logo">NKS WDC</div>
              <div class="about-version">v{{ appVersion }}</div>
              <div class="about-subtitle">Local development server manager</div>
              <div class="about-desc">
                A modern replacement for MAMP PRO, built with Electron + Vue 3 + Element Plus.
                Manages Apache, MySQL, PHP, Redis, Mailpit and SSL certificates for local development.
              </div>

              <!-- F85: Repo sources + docs — make the multi-repo architecture
                   discoverable from inside the app instead of only inside the
                   README on GitHub. -->
              <div class="about-links">
                <a href="https://github.com/nks-hub/webdev-console" target="_blank" class="about-link">webdev-console (app)</a>
                <a href="https://github.com/nks-hub/webdev-console-plugins" target="_blank" class="about-link">plugins</a>
                <a href="https://github.com/nks-hub/wdc-catalog-api" target="_blank" class="about-link">catalog-api</a>
                <a href="https://github.com/nks-hub/webdev-console-binaries" target="_blank" class="about-link">binaries</a>
                <a href="https://wdc.nks-hub.cz" target="_blank" class="about-link">Website</a>
              </div>

              <div class="about-stack">
                <el-tag size="small" effect="plain">Electron 34</el-tag>
                <el-tag size="small" effect="plain">Vue 3.5</el-tag>
                <el-tag size="small" effect="plain">Element Plus 2.9</el-tag>
                <el-tag size="small" effect="plain">.NET 9</el-tag>
              </div>

              <!-- F91.4: SSO login moved to Account tab (was here pre-F91.4).
                   Catalog status row stays — it's runtime info, not auth. -->
              <div v-if="pluginCatalogStatus" class="about-sso-status">
                <span :class="['status-dot', pluginCatalogStatus.lastFetch ? 'ok' : 'err']"></span>
                <span class="sys-label">{{ $t('settings.sso.catalog') }}</span>
                <span class="sys-value">
                  {{ pluginCatalogStatus.lastFetch
                      ? $t('settings.sso.catalogReachable', { ago: formatAgo(pluginCatalogStatus.lastFetch) })
                      : $t('settings.sso.catalogNeverSynced') }}
                </span>
                <!-- Task 02: when catalog has never synced, surface the
                     "Sync now" action inline so the user doesn't have to
                     navigate to the Plugins tab to understand why. -->
                <el-button
                  v-if="!pluginCatalogStatus.lastFetch"
                  size="small"
                  type="primary"
                  plain
                  :loading="refreshingCatalog"
                  style="margin-left: 8px"
                  @click="refreshCatalogNow"
                >{{ $t('settings.advanced.refreshCatalog') }}</el-button>
              </div>

              <div v-if="systemInfo" class="about-system">
                <div class="about-sys-title">Runtime</div>
                <!-- F85: daemon uptime + PID surfaced so user can see the
                     F90 fix reporting sane values (uptime since daemon start,
                     not system boot). -->
                <div v-if="systemInfo.daemon?.uptime !== undefined" class="about-sys-row">
                  <span class="sys-label">Daemon uptime</span>
                  <span class="sys-value">{{ formatUptime(systemInfo.daemon.uptime) }}</span>
                </div>
                <div v-if="systemInfo.daemon?.pid" class="about-sys-row">
                  <span class="sys-label">Daemon PID</span>
                  <span class="sys-value mono">{{ systemInfo.daemon.pid }}</span>
                </div>
                <div v-if="systemInfo.daemon?.version" class="about-sys-row">
                  <span class="sys-label">Daemon version</span>
                  <span class="sys-value mono">{{ systemInfo.daemon.version }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">Services</span>
                  <span class="sys-value">{{ systemInfo.services?.running }}/{{ systemInfo.services?.total }} running</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">Sites</span>
                  <span class="sys-value">{{ systemInfo.sites }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">Plugins</span>
                  <span class="sys-value">{{ systemInfo.plugins }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">Binaries</span>
                  <span class="sys-value">{{ systemInfo.binaries }}</span>
                </div>
                <div v-if="installedVersions.length" class="about-sys-title" style="margin-top: 12px">Installed Versions</div>
                <div v-for="bin in installedVersions" :key="bin.app" class="about-sys-row">
                  <span class="sys-label">{{ bin.app }}</span>
                  <span class="sys-value">{{ bin.version }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">OS</span>
                  <span class="sys-value">{{ systemInfo.os?.version }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">.NET</span>
                  <span class="sys-value">{{ systemInfo.runtime?.dotnet }} ({{ systemInfo.runtime?.arch }})</span>
                </div>
              </div>
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>

      <!-- Save button (not shown on About or Update) -->
      <div class="settings-footer" v-if="activeTab !== 'about' && activeTab !== 'update'">
        <el-button type="primary" size="small" :loading="saving" @click="save">
          {{ $t('common.save') }} {{ $t('common.settings') }}
        </el-button>
        <el-button size="small" @click="loadSettings">{{ $t('common.reset') }}</el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Upload, Download } from '@element-plus/icons-vue'
import { useThemeStore, type ThemeMode } from '../../stores/theme'
import { useUiModeStore } from '../../stores/uiMode'
import { useAuthStore } from '../../stores/auth'
import {
  catalogRegister, catalogLogin, fetchDevices, pushConfigToDevice,
  daemonBaseUrl, daemonAuthHeaders as authHeaders,
  fetchPhpVersions, fetchSettings, saveSettings,
  type DeviceInfo as CatalogDeviceInfo,
  type SystemInfo,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { compareSemver } from '../../utils/semver'

const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'
const themeStore = useThemeStore()
const uiModeStore = useUiModeStore()
const authStore = useAuthStore()

async function ssoLogin() {
  const url = catalogUrl.value || 'https://wdc.nks-hub.cz'
  try {
    await authStore.login(url)
    // F91.9: immediately pull the authoritative profile so the UI can
    // switch from "Signed in" to "Signed in as x@y.cz" without waiting
    // for the user to navigate away and back.
    await authStore.refreshProfile(url)
    ElMessage.success(authStore.displayName
      ? `Signed in as ${authStore.displayName}`
      : 'Signed in')
  } catch (err) {
    ElMessage.error(`SSO failed: ${errorMessage(err)}`)
  }
}

// F91.9: if the user already had a token on page load, also fetch the
// profile so a reload doesn't lose the "Signed in as" display.
// NOTE: catalogUrl is declared further down in this same setup block —
// we can't reference its .value at script-top. Defer to a microtask
// so the ref is initialized by the time we read it.
queueMicrotask(() => {
  if (authStore.isAuthenticated && !authStore.profile) {
    void authStore.refreshProfile(catalogUrl.value || 'https://wdc.nks-hub.cz')
  }
})
const { locale, t } = useI18n()

function onLocaleChange(v: string) {
  locale.value = v
  localStorage.setItem('wdc-locale', v)
}

const activeTab = ref('general')

const ADVANCED_ONLY_TABS = new Set(['ports', 'paths', 'databases', 'advanced', 'sync'])
watch(() => uiModeStore.isSimple, (simple) => {
  if (simple && ADVANCED_ONLY_TABS.has(activeTab.value)) {
    activeTab.value = 'general'
  }
})

const saving = ref(false)
const databases = ref<string[]>([])
const newDbName = ref('')
const systemInfo = ref<SystemInfo | null>(null)
const installedVersions = ref<Array<{ app: string; version: string }>>([])

const ports = reactive({
  http: 80,
  https: 443,
  mysql: 3306,
  redis: 6379,
  mailpitSmtp: 1025,
  mailpitHttp: 8025,
})

const runOnStartup = ref(false)
// Auto-start is now per-plugin only — toggle lives on each plugin card in
// Plugin Manager. The daemon still reads `service.<id>.autoStart` the same
// way, so the settings key format hasn't changed — just the UI surface.
const defaultPhp = ref('8.4')
const phpVersions = ref<string[]>(['8.4', '8.3', '7.4'])
const flushingDns = ref(false)
const mampDiscovering = ref(false)

// F79: open native OS file/folder picker + write result into paths[key].
// Electron-only (wrapped via preload's electronAPI.showOpenDialog). When
// running outside Electron the button no-ops with a warning toast.
async function browsePath(key: keyof typeof paths, kind: 'file' | 'folder'): Promise<void> {
  const api = window.electronAPI
  if (!api?.showOpenDialog) {
    ElMessage.warning('Native file dialog is only available in the packaged app')
    return
  }
  const result = await api.showOpenDialog({
    properties: kind === 'folder' ? ['openDirectory'] : ['openFile'],
    title: kind === 'folder' ? 'Select directory' : 'Select file',
    defaultPath: paths[key] || undefined,
  })
  if (result?.canceled) return
  const picked = result?.filePaths?.[0]
  if (typeof picked === 'string' && picked.length > 0) {
    paths[key] = picked
  }
}

// F85: format daemon uptime seconds into human-friendly string.
function formatUptime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '—'
  const s = Math.floor(seconds)
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ${s % 60}s`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ${m % 60}m`
  const d = Math.floor(h / 24)
  return `${d}d ${h % 24}h`
}

const paths = reactive({
  apache: '',
  mysql: '',
  php: '',
  redis: '',
  sitesDir: '',
  hostsFile: '',
})

// ── Additional settings from SPEC ─────────────────────────────────────
const phpFpmBasePort = ref(9000)
const telemetryEnabled = ref(false)
const telemetryCrashReports = ref(false)
const backupDir = ref('')
const backupScheduleHours = ref(0)

// Backup management
import { fetchBackups, createBackup, downloadBackup, type BackupEntry } from '../../api/daemon'
const backupsList = ref<BackupEntry[]>([])
const backupsLoading = ref(false)
const backupCreating = ref(false)

async function loadBackups() {
  backupsLoading.value = true
  try {
    const data = await fetchBackups()
    backupsList.value = data.backups
  } catch { backupsList.value = [] }
  finally { backupsLoading.value = false }
}

async function manualBackup() {
  backupCreating.value = true
  try {
    const result = await createBackup()
    ElMessage.success(`Backup created: ${result.files} files, ${(result.size / 1024 / 1024).toFixed(1)} MB`)
    void loadBackups()
  } catch (e) {
    ElMessage.error(`Backup failed: ${errorMessage(e)}`)
  } finally {
    backupCreating.value = false
  }
}

function downloadBackupFile(path: string) {
  downloadBackup(path)
}

// ── Danger-zone reset operations ──────────────────────────────────────
const resettingSettings = ref(false)
const resettingFactory = ref(false)

async function confirmResetSettings() {
  try {
    await ElMessageBox.confirm(
      'Opravdu smazat tabulku `settings`? Weby, databáze a binárky zůstanou. ' +
      'WDC se po operaci restartuje a uvítací průvodce proběhne znovu.',
      'Reset nastavení',
      { type: 'warning', confirmButtonText: 'Smazat a restartovat', cancelButtonText: 'Zrušit' },
    )
  } catch { return }
  resettingSettings.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/admin/reset?scope=settings`, {
      method: 'POST', headers: authHeaders(),
    })
    if (!r.ok) throw new Error(`HTTP ${r.status}: ${await r.text()}`)
    ElMessage.success('Nastavení smazáno. Daemon se restartuje…')
    // Daemon exits with code 99 → Electron main respawns. Token in
    // localStorage survives but will be stale until next login.
  } catch (e) {
    ElMessage.error(`Reset selhal: ${errorMessage(e)}`)
  } finally {
    resettingSettings.value = false
  }
}

async function confirmFactoryReset() {
  try {
    await ElMessageBox.confirm(
      'SMAZAT VŠECHNO? To zahrnuje všechny weby, databáze i vazby pluginů. ' +
      'Binárky (~/.wdc/binaries/) zůstanou, aby se znovu nestahovalo Apache/PHP/MySQL. ' +
      'Tato operace je NEVRATNÁ.',
      'Tovární reset',
      { type: 'error', confirmButtonText: 'Ano, smazat všechno', cancelButtonText: 'Zrušit', distinguishCancelAndClose: true },
    )
  } catch { return }
  resettingFactory.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/admin/reset?scope=factory`, {
      method: 'POST', headers: authHeaders(),
    })
    if (!r.ok) throw new Error(`HTTP ${r.status}: ${await r.text()}`)
    ElMessage.success('Factory reset proveden. Daemon se restartuje…')
  } catch (e) {
    ElMessage.error(`Reset selhal: ${errorMessage(e)}`)
  } finally {
    resettingFactory.value = false
  }
}

// ── Catalog API integration (Advanced tab) ────────────────────────────
const catalogUrl = ref('')
const pluginAutoSync = ref(false)
const syncingPlugins = ref(false)
const pluginSyncStatus = ref<{ ok: boolean; message: string } | null>(null)
const mysqlRootPassword = ref('')
const mysqlRootExists = ref(false)
const mysqlRootSaving = ref(false)

async function loadMysqlRootStatus() {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/root-password`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      mysqlRootExists.value = !!data?.exists
    }
  } catch { /* daemon unreachable — default false */ }
}

async function saveMysqlRootPassword() {
  if (!mysqlRootPassword.value) return
  mysqlRootSaving.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/root-password`, {
      method: 'POST',
      headers: { ...authHeaders(), 'content-type': 'application/json' },
      body: JSON.stringify({ password: mysqlRootPassword.value }),
    })
    if (!r.ok) throw new Error(await r.text().catch(() => `HTTP ${r.status}`))
    mysqlRootPassword.value = ''
    mysqlRootExists.value = true
    ElMessage.success('MySQL root password saved (DPAPI-encrypted)')
  } catch (e) {
    ElMessage.error(`Save failed: ${errorMessage(e)}`)
  } finally {
    mysqlRootSaving.value = false
  }
}
const pluginCatalogStatus = ref<{ catalogCount: number; cachedCount: number; lastFetch: string | null; cacheRoot: string } | null>(null)

// Task 15: plugin-declared ports (IPortMetadata DI registrations).
// Populated from GET /api/plugins/ports. Only contains active plugins.
const pluginPorts = ref<Array<{
  key: string
  label: string
  pluginId: string
  defaultPort: number
  currentPort: number
  isActive: boolean
}>>([])

// Task 03: snapshots state + actions.
function formatDate(s: string | number | null | undefined): string {
  if (!s) return '—'
  const d = new Date(s)
  return isNaN(d.getTime()) ? String(s) : d.toLocaleString()
}

interface SyncSnapshot {
  id: number
  device_id: string
  created_at: string
  size_bytes: number
}
const snapshots = ref<SyncSnapshot[]>([])
const snapshotsLoading = ref(false)
const snapshotAction = ref<number | null>(null)

async function loadSnapshots() {
  if (!accountToken.value) { snapshots.value = []; return }
  snapshotsLoading.value = true
  try {
    const url = getCatalogUrl()
    const r = await fetch(`${url}/api/v1/sync/snapshots`, {
      headers: { Authorization: `Bearer ${accountToken.value}` },
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const data = await r.json() as { snapshots: SyncSnapshot[] }
    snapshots.value = data.snapshots ?? []
  } catch (e) {
    ElMessage.error(`Failed to load snapshots: ${errorMessage(e)}`)
  } finally {
    snapshotsLoading.value = false
  }
}

async function restoreSnapshot(row: SyncSnapshot) {
  try {
    await ElMessageBox.confirm(
      `Restore settings from snapshot taken ${formatDate(row.created_at)}? Local changes that haven't been pushed will be overwritten.`,
      'Restore snapshot',
      { type: 'warning', confirmButtonText: 'Restore', cancelButtonText: 'Cancel' },
    )
  } catch { return /* cancelled */ }
  snapshotAction.value = row.id
  try {
    const url = getCatalogUrl()
    const r = await fetch(`${url}/api/v1/sync/snapshots/${row.id}/restore`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${accountToken.value}` },
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const data = await r.json()
    // Route the payload through the same merge logic as pullFromCloud —
    // saveSettings + reload.
    if (data?.payload?.settings) {
      await saveSettings(data.payload.settings as Record<string, string>)
      await loadSettings()
    }
    ElMessage.success('Snapshot restored')
  } catch (e) {
    ElMessage.error(`Restore failed: ${errorMessage(e)}`)
  } finally {
    snapshotAction.value = null
  }
}

async function deleteSnapshot(row: SyncSnapshot) {
  try {
    await ElMessageBox.confirm(
      `Delete this snapshot? This cannot be undone.`,
      'Delete snapshot',
      { type: 'warning', confirmButtonText: 'Delete', cancelButtonText: 'Cancel' },
    )
  } catch { return }
  snapshotAction.value = row.id
  try {
    const url = getCatalogUrl()
    const r = await fetch(`${url}/api/v1/sync/snapshots/${row.id}`, {
      method: 'DELETE',
      headers: { Authorization: `Bearer ${accountToken.value}` },
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    snapshots.value = snapshots.value.filter(s => s.id !== row.id)
    ElMessage.success('Snapshot deleted')
  } catch (e) {
    ElMessage.error(`Delete failed: ${errorMessage(e)}`)
  } finally {
    snapshotAction.value = null
  }
}

async function loadPluginPorts() {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/ports`, { headers: authHeaders() })
    if (!r.ok) return
    const data = await r.json() as Array<{
      key: string; label: string; pluginId: string
      defaultPort: number; currentPort: number; isActive: boolean
    }>
    // Filter inactive — per user decision (variant B in interview).
    pluginPorts.value = data.filter(p => p.isActive)
  } catch { /* no-op, section just doesn't render */ }
}

async function loadPluginCatalogStatus() {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/catalog/status`, { headers: authHeaders() })
    if (r.ok) pluginCatalogStatus.value = await r.json()
  } catch { /* daemon unreachable — leave as null so the hint line hides */ }
}

function formatAgo(iso: string): string {
  const then = new Date(iso).getTime()
  const diffSec = Math.max(0, Math.floor((Date.now() - then) / 1000))
  if (diffSec < 60) return `${diffSec}s ago`
  if (diffSec < 3600) return `${Math.floor(diffSec / 60)}m ago`
  if (diffSec < 86400) return `${Math.floor(diffSec / 3600)}h ago`
  return `${Math.floor(diffSec / 86400)}d ago`
}
const refreshingCatalog = ref(false)
const testingCatalog = ref(false)
const catalogStatus = ref<{ ok: boolean; message: string } | null>(null)

async function loadDatabases() {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      databases.value = data.databases ?? []
    }
  } catch { /* not connected */ }
}

async function createDatabase() {
  if (!newDbName.value) return
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ name: newDbName.value }),
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    ElMessage.success(`Database ${newDbName.value} created`)
    newDbName.value = ''
    await loadDatabases()
  } catch (e) { ElMessage.error(`Create failed: ${errorMessage(e)}`) }
}

async function dropDatabase(name: string) {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${name}`, { method: 'DELETE', headers: authHeaders() })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    ElMessage.success(`Database ${name} dropped`)
    await loadDatabases()
  } catch (e) { ElMessage.error(`Drop failed: ${errorMessage(e)}`) }
}

async function flushDns() {
  flushingDns.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/dns/flush`, { method: 'POST', headers: authHeaders() })
    if (r.ok) ElMessage.success('DNS cache flushed')
    else ElMessage.warning('DNS flush may require admin privileges')
  } catch {
    ElMessage.warning('DNS flush endpoint not available')
  } finally {
    flushingDns.value = false
  }
}

// F73: MAMP PRO discovery + import relocated from Sites toolbar to Settings
// General tab. Two-step flow: discover-mamp enumerates candidate vhosts on
// disk, then migrate-mamp imports them as WDC sites after user confirmation.
async function discoverMamp() {
  mampDiscovering.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/sites/discover-mamp`, { headers: authHeaders() })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const data: { count?: number; sites?: Array<{ domain?: string; Domain?: string }> } = await r.json()
    if (!data.count || data.count === 0) {
      ElMessage.info('No MAMP PRO sites found on this machine')
      return
    }
    const confirmed = await ElMessageBox.confirm(
      `Found ${data.count} MAMP site(s): ${(data.sites ?? []).map(s => s.domain || s.Domain).join(', ')}. Import them?`,
      'MAMP Migration',
      { confirmButtonText: 'Import', cancelButtonText: 'Cancel', type: 'info' },
    )
    if (confirmed) {
      const ir = await fetch(`${daemonBaseUrl()}/api/sites/migrate-mamp`, { method: 'POST', headers: authHeaders() })
      if (!ir.ok) throw new Error((await ir.text().catch(() => '')) || `HTTP ${ir.status}`)
      const result = await ir.json()
      ElMessage.success(`Imported ${result.count} site(s) from MAMP`)
    }
  } catch (e) {
    if (e !== 'cancel') ElMessage.error(`MAMP migration: ${errorMessage(e)}`)
  } finally {
    mampDiscovering.value = false
  }
}

async function loadPhpVersions() {
  try {
    const data = await fetchPhpVersions()
    phpVersions.value = data.map(v => v.majorMinor || v.version)
  } catch { /* keep defaults */ }
}

async function loadSettings() {
  try {
    const data = await fetchSettings()
    if (data['ports.http'])        ports.http = parseInt(data['ports.http'])
    if (data['ports.https'])       ports.https = parseInt(data['ports.https'])
    if (data['ports.mysql'])       ports.mysql = parseInt(data['ports.mysql'])
    if (data['ports.redis'])       ports.redis = parseInt(data['ports.redis'])
    if (data['ports.mailpitSmtp']) ports.mailpitSmtp = parseInt(data['ports.mailpitSmtp'])
    if (data['ports.mailpitHttp']) ports.mailpitHttp = parseInt(data['ports.mailpitHttp'])
    if (data['general.runOnStartup']) runOnStartup.value = data['general.runOnStartup'] === 'true'
    if (data['paths.apache'])    paths.apache = data['paths.apache']
    if (data['paths.mysql'])     paths.mysql = data['paths.mysql']
    if (data['paths.php'])       paths.php = data['paths.php']
    if (data['paths.redis'])     paths.redis = data['paths.redis']
    if (data['paths.sitesDir'])  paths.sitesDir = data['paths.sitesDir']
    if (data['paths.hostsFile']) paths.hostsFile = data['paths.hostsFile']
    if (data['ports.phpFpmBase']) phpFpmBasePort.value = parseInt(data['ports.phpFpmBase'])
    if (data['daemon.catalogUrl']) catalogUrl.value = data['daemon.catalogUrl']
    if (data['plugins.autoSyncEnabled']) pluginAutoSync.value = data['plugins.autoSyncEnabled'] === 'true'
    if (data['telemetry.enabled']) telemetryEnabled.value = data['telemetry.enabled'] === 'true'
    if (data['telemetry.crashReports']) telemetryCrashReports.value = data['telemetry.crashReports'] === 'true'
    if (data['backup.dir']) backupDir.value = data['backup.dir']
    if (data['backup.scheduleHours']) backupScheduleHours.value = parseInt(data['backup.scheduleHours'])
  } catch { /* daemon not reachable — keep defaults */ }
}

async function syncPluginsNow() {
  syncingPlugins.value = true
  pluginSyncStatus.value = null
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/catalog/sync`, {
      method: 'POST',
      headers: authHeaders(),
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const result = await r.json()
    const msg = `Catalog: ${result.catalogCount ?? 0} plugins · installed this run: ${result.installedThisCall ?? 0}`
    pluginSyncStatus.value = { ok: true, message: msg }
    ElMessage.success(msg)
    // Re-poll the status endpoint so the "last sync" label reflects the
    // freshly-completed refresh without a tab navigation.
    await loadPluginCatalogStatus()
  } catch (e) {
    const msg = errorMessage(e)
    pluginSyncStatus.value = { ok: false, message: msg }
    ElMessage.error(`Plugin sync failed: ${msg}`)
  } finally {
    syncingPlugins.value = false
  }
}

async function refreshCatalog() {
  refreshingCatalog.value = true
  catalogStatus.value = null
  try {
    // Save URL first so the daemon's CatalogClient picks it up, then
    // trigger a manual refresh so the new source takes effect without
    // restarting the daemon.
    await saveSettings({ 'daemon.catalogUrl': catalogUrl.value || '' })
    const r = await fetch(`${daemonBaseUrl()}/api/binaries/catalog/refresh`, {
      method: 'POST',
      headers: authHeaders(),
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const result = await r.json()
    catalogStatus.value = {
      ok: true,
      message: `Refreshed: ${result.count ?? 0} releases · ${result.lastFetch ?? 'now'}`,
    }
    ElMessage.success(`Catalog refreshed: ${result.count ?? 0} releases`)
  } catch (e) {
    const msg = errorMessage(e)
    catalogStatus.value = { ok: false, message: `Refresh failed: ${msg}` }
    ElMessage.error(`Refresh failed: ${msg}`)
  } finally {
    refreshingCatalog.value = false
  }
}

// Task 02: inline CTA on About tab — hits both binary + plugin catalog
// so a single click fixes "nesynchronizováno" regardless of which one
// is actually stale. Reuses the two existing per-catalog refreshers.
async function refreshCatalogNow() {
  refreshingCatalog.value = true
  try {
    await Promise.allSettled([refreshCatalog(), syncPluginsNow()])
    await loadPluginCatalogStatus()
  } finally {
    refreshingCatalog.value = false
  }
}

async function testCatalogReachable() {
  testingCatalog.value = true
  catalogStatus.value = null
  const url = catalogUrl.value || 'https://wdc.nks-hub.cz'
  try {
    const r = await fetch(`${url.replace(/\/$/, '')}/healthz`)
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const body = await r.json()
    catalogStatus.value = {
      ok: true,
      message: `Reachable: ${body.service ?? 'catalog-api'} v${body.version ?? '?'}`,
    }
  } catch (e) {
    catalogStatus.value = {
      ok: false,
      message: `Unreachable: ${errorMessage(e)}. Is the sidecar running?`,
    }
  } finally {
    testingCatalog.value = false
  }
}

function openCatalogAdmin() {
  const url = catalogUrl.value || 'https://wdc.nks-hub.cz'
  window.open(url.replace(/\/$/, '') + '/admin', '_blank')
}

// ── Account & Devices tab ─────────────────────────────────────────────
// F91.15b: single source of truth = authStore.token. Cloud Sync (Push/
// Pull to catalog) used to read a separate `nks-wdc-catalog-jwt` set by
// the password login form, while SSO wrote its token to
// `nks-wdc-sso-token`. Signing in via SSO left accountToken empty so
// Push/Pull failed with 401 even though Sidebar showed "Signed in".
// Both login paths now feed authStore.setToken(); Push/Pull reads from
// the same store. The old `nks-wdc-catalog-jwt` key is migrated on load
// so users who signed in via password before this fix aren't logged out.
const _legacyJwt = localStorage.getItem('nks-wdc-catalog-jwt') || ''
if (_legacyJwt && !authStore.token) {
  authStore.setToken(_legacyJwt)
  localStorage.removeItem('nks-wdc-catalog-jwt')
}
const accountToken = computed({
  get: () => authStore.token,
  set: (v: string) => { authStore.setToken(v) },
})
const accountEmail = computed(() => authStore.displayName || localStorage.getItem('nks-wdc-catalog-email') || '')
const authEmail = ref('')
const authPassword = ref('')
const authLoading = ref(false)
const authError = ref('')
const accountDevices = ref<CatalogDeviceInfo[]>([])
const devicesLoading = ref(false)
const pushingTo = ref<string | null>(null)
// Task 07: track which device is currently being unlinked so its button
// stays spinning without blocking the rest of the table.
const unlinkingDevice = ref<string | null>(null)
const editingDeviceName = ref<string | null>(null)

async function unlinkDevice(row: CatalogDeviceInfo) {
  try {
    await ElMessageBox.confirm(
      `Unlink ${row.name || row.device_id.slice(0, 12)} from your account? That device will need to sign in again to sync.`,
      'Unlink device',
      { type: 'warning', confirmButtonText: 'Unlink', cancelButtonText: 'Cancel' },
    )
  } catch { return /* user cancelled */ }

  unlinkingDevice.value = row.device_id
  try {
    const url = getCatalogUrl()
    const r = await fetch(`${url}/api/v1/devices/${row.device_id}`, {
      method: 'DELETE',
      headers: { Authorization: `Bearer ${accountToken.value}` },
    })
    if (!r.ok) {
      const body = await r.json().catch(() => null) as { detail?: string } | null
      throw new Error(body?.detail || `HTTP ${r.status}`)
    }
    ElMessage.success('Device unlinked')
    await loadDevicesAccount()
  } catch (e) {
    ElMessage.error(`Unlink failed: ${errorMessage(e)}`)
  } finally {
    unlinkingDevice.value = null
  }
}
const editingDeviceValue = ref('')

function startEditDeviceName(row: CatalogDeviceInfo) {
  editingDeviceName.value = row.device_id
  editingDeviceValue.value = row.name || ''
}

async function saveDeviceName(row: CatalogDeviceInfo) {
  const newName = editingDeviceValue.value.trim()
  editingDeviceName.value = null
  if (newName === (row.name || '')) return
  try {
    const url = getCatalogUrl()
    const r = await fetch(`${url}/api/v1/devices/${row.device_id}?name=${encodeURIComponent(newName)}`, {
      method: 'PUT',
      headers: { Authorization: `Bearer ${accountToken.value}` },
    })
    if (!r.ok) {
      // Surface catalog-api's detail body so auth/validation failures
      // show "Not authenticated" / "Device not found" etc instead of
      // a bare HTTP status that gives zero hint about the fix.
      const body = await r.json().catch(() => null) as { detail?: string } | null
      throw new Error(body?.detail || `HTTP ${r.status}`)
    }
    row.name = newName
    ElMessage.success('Device renamed')
  } catch (e) {
    ElMessage.error(`Rename failed: ${errorMessage(e)}`)
  }
}

function getCatalogUrl(): string {
  return (catalogUrl.value || 'https://wdc.nks-hub.cz').replace(/\/$/, '')
}

async function doLogin() {
  authLoading.value = true
  authError.value = ''
  try {
    const result = await catalogLogin(getCatalogUrl(), authEmail.value, authPassword.value)
    // F91.15b: single token store — writes go straight through authStore
    // so the sidebar + /sync endpoints all see the same session.
    authStore.setToken(result.token)
    localStorage.setItem('nks-wdc-catalog-email', result.email)
    // Mirror the token into daemon SettingsStore so the background
    // CatalogHeartbeatService (C#) can keep last_seen_at fresh between
    // manual pushes — without this, daemon's GetString("sync","accountToken")
    // returns null and the heartbeat loop is a no-op even though the
    // renderer is authenticated.
    await saveSettings({
      'sync.accountToken': result.token,
      'sync.accountEmail': result.email,
    })
    await authStore.refreshProfile(getCatalogUrl())
    authPassword.value = ''
    ElMessage.success(`Signed in as ${result.email}`)

    // Auto-register this device in the catalog: catalog-api creates a
    // DeviceConfig row lazily on the first /sync/push, so without this
    // the freshly-logged-in user sees an empty devices table until they
    // manually click "Push". Fire-and-forget — any error surfaces in the
    // devices table reload below rather than blocking login UI.
    void autoRegisterDeviceAfterLogin()

    void loadDevicesAccount()
  } catch (e) {
    authError.value = errorMessage(e)
  } finally {
    authLoading.value = false
  }
}

// Silent version of pushToCloud used right after login so the first
// devices list render isn't empty. Same endpoint + payload shape, but
// no ElMessage toast on success (the login toast is enough) and errors
// are only logged — we don't want a failed first-push to look like
// login itself failed.
async function autoRegisterDeviceAfterLogin() {
  if (!accountToken.value) return
  try {
    // Make sure we have a persisted deviceId before pushing. Fresh
    // installs generate + save one here so the server-side row keys
    // correctly.
    if (!deviceId.value) await loadDeviceId()
    if (!deviceId.value) return

    const payload = await buildSyncPayload()
    const proxyHeaders = authHeaders()
    proxyHeaders['X-Catalog-Token'] = accountToken.value
    const r = await fetch(`${daemonBaseUrl()}/api/sync/push`, {
      method: 'POST',
      headers: proxyHeaders,
      body: JSON.stringify({ device_id: deviceId.value, payload }),
    })
    if (r.ok) {
      lastSyncTime.value = new Date().toISOString()
      await saveSettings({ 'sync.lastSyncTime': lastSyncTime.value })
    }
    // Non-ok is silent by design — next manual push / heartbeat will
    // retry and surface the error with proper context.
  } catch {
    /* network/daemon hiccup — silent, heartbeat loop retries */
  }
}

async function doRegister() {
  authLoading.value = true
  authError.value = ''
  try {
    const result = await catalogRegister(getCatalogUrl(), authEmail.value, authPassword.value)
    authStore.setToken(result.token)
    localStorage.setItem('nks-wdc-catalog-email', result.email)
    await authStore.refreshProfile(getCatalogUrl())
    authPassword.value = ''
    ElMessage.success(`Account created: ${result.email}`)
  } catch (e) {
    authError.value = errorMessage(e)
  } finally {
    authLoading.value = false
  }
}

async function doLogout() {
  // F91.15b: single-source logout — authStore.logout() clears token +
  // profile; legacy localStorage keys are best-effort swept too.
  authStore.logout()
  localStorage.removeItem('nks-wdc-catalog-jwt')
  localStorage.removeItem('nks-wdc-catalog-email')
  accountDevices.value = []
  // Clear the daemon-side mirror so CatalogHeartbeatService stops
  // pinging the catalog with a now-invalid token. saveSettings with an
  // empty string is treated as "unset" by the daemon's GetString guard.
  try {
    await saveSettings({ 'sync.accountToken': '', 'sync.accountEmail': '' })
  } catch { /* daemon unreachable, heartbeat will just retry 401 once */ }
  ElMessage.success('Signed out')
}

async function loadDevicesAccount() {
  if (!accountToken.value) return
  devicesLoading.value = true
  try {
    accountDevices.value = await fetchDevices(
      getCatalogUrl(),
      accountToken.value,
      deviceId.value || undefined,
    )
  } catch (e) {
    const msg = errorMessage(e)
    ElMessage.error(`Load devices failed: ${msg}`)
    // Auto-logout on auth failure — use case-insensitive match and
    // check for common catalog-api auth wording so a 403 / "Not
    // authenticated" / "token expired" message all trigger cleanup.
    if (/401|403|unauthori[sz]ed|not authenticated|token/i.test(msg)) {
      doLogout()
    }
  } finally {
    devicesLoading.value = false
  }
}

async function pushMyConfigTo(targetDeviceId: string) {
  if (!accountToken.value || !deviceId.value) return
  pushingTo.value = targetDeviceId
  try {
    await pushConfigToDevice(getCatalogUrl(), accountToken.value, targetDeviceId, deviceId.value)
    ElMessage.success(`Config pushed to device ${targetDeviceId.slice(0, 8)}…`)
  } catch (e) {
    ElMessage.error(`Push failed: ${errorMessage(e)}`)
  } finally {
    pushingTo.value = null
  }
}

// ── Sync tab state ────────────────────────────────────────────────────
const deviceId = ref('')
const deviceName = ref('')
const syncing = ref(false)
const pulling = ref(false)
const checkingCloud = ref(false)
const syncStatus = ref<{ ok: boolean; message: string } | null>(null)
const lastSyncTime = ref<string | null>(null)
const importFileInput = ref<HTMLInputElement | null>(null)

// Render lastSyncTime consistently regardless of whether it came from
// sync.lastSyncTime in settings (ISO format) or a fresh push (toLocaleString
// used to be stored directly). Try parsing as Date first; fall back to the
// raw string if parse fails so legacy locale-formatted values still show.
const lastSyncDisplay = computed(() => {
  if (!lastSyncTime.value) return ''
  const parsed = new Date(lastSyncTime.value)
  if (isNaN(parsed.getTime())) return lastSyncTime.value
  return parsed.toLocaleString()
})

async function loadDeviceId() {
  // Device ID is persisted in daemon settings; generate if missing
  try {
    const data = await fetchSettings()
    if (data['sync.deviceId']) {
      deviceId.value = data['sync.deviceId']
    } else {
      // First run: generate a UUID and persist it
      const id = crypto.randomUUID()
      deviceId.value = id
      await saveSettings({ 'sync.deviceId': id })
    }
    if (data['sync.deviceName']) deviceName.value = data['sync.deviceName']
    if (data['sync.lastSyncTime']) lastSyncTime.value = data['sync.lastSyncTime']
  } catch { /* daemon not reachable */ }
}

function copyDeviceId() {
  navigator.clipboard.writeText(deviceId.value)
    .then(() => ElMessage.success('Device ID copied'))
    .catch(() => ElMessage.warning('Cannot access clipboard'))
}

async function buildSyncPayload(): Promise<Record<string, unknown>> {
  // Collect settings + sites + system info so the catalog-api can
  // populate the device fleet table with OS/arch/site count without
  // the user having to enter them manually.
  //
  // CRITICAL: both `settings` and `sites` are filtered through the same
  // sync/local classification the pull side uses. Without this filter,
  // local-only fields (absolute paths like C:\work\htdocs\project, ports
  // like 8081, documentRoot) would get uploaded to the shared catalog,
  // leaking machine-specific paths and polluting the stored snapshot
  // with values that the pull side would refuse to apply anyway.
  const [rawSettings, sitesRes, systemRes] = await Promise.all([
    fetchSettings().catch(() => ({} as Record<string, string>)),
    fetch(`${daemonBaseUrl()}/api/sites`, { headers: authHeaders() }),
    fetch(`${daemonBaseUrl()}/api/system`, { headers: authHeaders() }),
  ])
  const rawSites: Array<{ domain: string } & Record<string, unknown>> = sitesRes.ok ? await sitesRes.json() : []
  const system = systemRes.ok ? await systemRes.json() : null

  // Filter settings: drop local-only keys (paths, ports, backup.dir)
  const settings: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(rawSettings)) {
    if (isSettingSyncable(key)) settings[key] = value
  }

  // Filter sites: keep only SITE_SYNC_FIELDS per site (plus domain as the key)
  const sites = rawSites.map(site => {
    const filtered: Record<string, unknown> = { domain: site.domain }
    for (const field of SITE_SYNC_FIELDS) {
      if (field in site) filtered[field] = site[field]
    }
    return filtered
  })

  return {
    exportedAt: new Date().toISOString(),
    version: appVersion,
    deviceId: deviceId.value,
    deviceName: deviceName.value,
    settings,
    sites,
    system,
  }
}

async function pushToCloud() {
  syncing.value = true
  syncStatus.value = null
  try {
    // Save device name first
    await saveSettings({
      'sync.deviceName': deviceName.value,
      'sync.lastSyncTime': new Date().toISOString(),
    })

    const payload = await buildSyncPayload()
    // Task 33: route through daemon proxy so catalog JWT stays server-side
    // and CORS is no longer a concern. Daemon adds its own 30s timeout.
    const proxyHeaders = authHeaders()
    if (accountToken.value) proxyHeaders['X-Catalog-Token'] = accountToken.value
    const r = await fetch(`${daemonBaseUrl()}/api/sync/push`, {
      method: 'POST',
      headers: proxyHeaders,
      body: JSON.stringify({ device_id: deviceId.value, payload }),
    })
    if (!r.ok) {
      const text = await r.text().catch(() => r.statusText)
      throw new Error(text || `HTTP ${r.status}`)
    }
    lastSyncTime.value = new Date().toISOString()
    syncStatus.value = { ok: true, message: 'Pushed successfully' }
    ElMessage.success('Configuration pushed to cloud')
  } catch (e) {
    const msg = errorMessage(e)
    syncStatus.value = { ok: false, message: `Push failed: ${msg}` }
    ElMessage.error(`Push failed: ${msg}`)
  } finally {
    syncing.value = false
  }
}

// ── Sync field classification (Strategy D) ────────────────────────────
// Each settings key is tagged as "sync" (portable across devices) or
// "local" (machine-specific paths/ports that must stay untouched on
// pull). The classification lives here so adding a new settings key
// forces the developer to decide "is this sync or local?" at definition
// time. Site fields follow the same principle: domain/phpVersion/ssl/
// aliases/framework/cloudflare are sync; documentRoot/ports are local.
const SYNC_SETTINGS_PREFIXES = [
  'general.', 'telemetry.', 'backup.scheduleHours', 'daemon.catalogUrl',
  'sync.',
]
const LOCAL_SETTINGS_PREFIXES = [
  'paths.', 'ports.', 'backup.dir',
]

function isSettingSyncable(key: string): boolean {
  if (SYNC_SETTINGS_PREFIXES.some(p => key.startsWith(p))) return true
  if (LOCAL_SETTINGS_PREFIXES.some(p => key.startsWith(p))) return false
  return true // unknown keys default to sync
}

const SITE_SYNC_FIELDS = new Set([
  'domain', 'phpVersion', 'sslEnabled', 'aliases', 'framework',
  'environment', 'cloudflare', 'nodeUpstreamPort', 'nodeStartCommand',
])
// Note: the inverse set (documentRoot/httpPort/httpsPort) is implicitly
// excluded by iterating SITE_SYNC_FIELDS only — no need for a second set.

async function pullFromCloud() {
  pulling.value = true
  syncStatus.value = null
  try {
    // Task 33: route through daemon proxy — removes CORS dependency, daemon
    // forwards Bearer token from X-Catalog-Token and enforces 30s timeout.
    const pullHeaders = authHeaders()
    if (accountToken.value) pullHeaders['X-Catalog-Token'] = accountToken.value
    const r = await fetch(`${daemonBaseUrl()}/api/sync/pull`, {
      method: 'POST',
      headers: pullHeaders,
      body: JSON.stringify({ device_id: deviceId.value }),
    })
    if (!r.ok) {
      if (r.status === 404) {
        syncStatus.value = { ok: false, message: 'No cloud snapshot found for this device' }
        ElMessage.info('No cloud snapshot found — push first')
        return
      }
      const text = await r.text().catch(() => r.statusText)
      throw new Error(text || `HTTP ${r.status}`)
    }
    const data = await r.json()
    const payload = data.payload

    // ── Merge settings with local overrides ──────────────────────────
    // Only apply sync-classified keys from the remote snapshot. Local
    // keys (paths, ports, backup dir) stay untouched so pulling another
    // device's snapshot doesn't overwrite C:\work\htdocs with /home/user.
    if (payload?.settings && typeof payload.settings === 'object') {
      const merged: Record<string, string> = {}
      for (const [key, value] of Object.entries(payload.settings as Record<string, string>)) {
        if (isSettingSyncable(key)) {
          merged[key] = value
        }
        // Local keys: keep existing local value (skip remote)
      }

      if (Object.keys(merged).length > 0) {
        await saveSettings(merged)
      }
    }

    // ── Merge sites ──────────────────────────────────────────────────
    // Match by domain. Existing sites: merge sync fields, keep local.
    // New sites: create with sync fields + empty documentRoot (user
    // must set it via SiteEdit before the vhost is generated).
    if (Array.isArray(payload?.sites)) {
      type SyncableSite = { domain: string; [k: string]: unknown }
      const localSites: SyncableSite[] = await fetch(`${daemonBaseUrl()}/api/sites`, { headers: authHeaders() })
        .then(r => r.ok ? r.json() : [])
      const localByDomain = new Map<string, SyncableSite>(localSites.map(s => [s.domain, s]))

      let newSiteCount = 0
      for (const remoteSite of payload.sites as SyncableSite[]) {
        const domain = remoteSite.domain
        if (!domain) continue
        const local = localByDomain.get(domain)

        if (local) {
          // Existing site: merge sync fields only
          const update: Record<string, unknown> = { ...local }
          for (const field of SITE_SYNC_FIELDS) {
            if (field in remoteSite) update[field] = remoteSite[field]
          }
          await fetch(`${daemonBaseUrl()}/api/sites/${domain}`, {
            method: 'PUT',
            headers: authHeaders(),
            body: JSON.stringify(update),
          })
        } else {
          // New site: create with sync fields. DocumentRoot can't be empty
          // (SiteManager.ValidateDocumentRoot throws) so we use a clear
          // placeholder path that passes validation. The user replaces it
          // in SiteEdit → General → Document Root before the vhost works.
          const placeholder = navigator.platform?.startsWith('Win')
            ? `C:\\pending-sync\\${domain}`
            : `/tmp/pending-sync/${domain}`
          const newSite: Record<string, unknown> = { domain, documentRoot: placeholder }
          for (const field of SITE_SYNC_FIELDS) {
            if (field in remoteSite) newSite[field] = remoteSite[field]
          }
          try {
            await fetch(`${daemonBaseUrl()}/api/sites`, {
              method: 'POST',
              headers: authHeaders(),
              body: JSON.stringify(newSite),
            })
            newSiteCount++
          } catch { /* domain validation may reject invalid entries */ }
        }
      }

      if (newSiteCount > 0) {
        ElMessage.info(`${newSiteCount} new site(s) imported — set their document root in Sites`)
      }
    }

    syncStatus.value = { ok: true, message: `Pulled from cloud (${data.updated_at ?? 'unknown'})` }
    ElMessage.success('Configuration synced from cloud')
    await loadSettings()
  } catch (e) {
    const msg = errorMessage(e)
    syncStatus.value = { ok: false, message: `Pull failed: ${msg}` }
    ElMessage.error(`Pull failed: ${msg}`)
  } finally {
    pulling.value = false
  }
}

async function checkCloudExists() {
  checkingCloud.value = true
  syncStatus.value = null
  try {
    // Task 33: route through daemon proxy.
    const existsHeaders = authHeaders()
    if (accountToken.value) existsHeaders['X-Catalog-Token'] = accountToken.value
    const r = await fetch(
      `${daemonBaseUrl()}/api/sync/exists?device_id=${encodeURIComponent(deviceId.value)}`,
      { headers: existsHeaders },
    )
    if (!r.ok) {
      const text = await r.text().catch(() => r.statusText)
      throw new Error(text || `HTTP ${r.status}`)
    }
    const data = await r.json()
    syncStatus.value = {
      ok: data.has_config,
      message: data.has_config
        ? `Cloud snapshot exists (updated ${data.updated_at ?? 'unknown'})`
        : 'No cloud snapshot for this device',
    }
  } catch (e) {
    syncStatus.value = { ok: false, message: `Check failed: ${errorMessage(e)}` }
  } finally {
    checkingCloud.value = false
  }
}

async function exportSettings() {
  try {
    const payload = await buildSyncPayload()
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `nks-wdc-settings-${new Date().toISOString().slice(0, 10)}.json`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success('Settings exported')
  } catch (e) {
    ElMessage.error(`Export failed: ${errorMessage(e)}`)
  }
}

function triggerImport() {
  importFileInput.value?.click()
}

async function importSettings(event: Event) {
  const file = (event.target as HTMLInputElement)?.files?.[0]
  if (!file) return
  try {
    const text = await file.text()
    const data = JSON.parse(text)
    if (!data.settings || typeof data.settings !== 'object') {
      throw new Error('Invalid settings file — missing "settings" object')
    }
    const fromDevice = data.deviceId ?? 'unknown'
    const fromDate = data.exportedAt ? new Date(data.exportedAt).toLocaleString() : 'unknown'
    await ElMessageBox.confirm(
      `Import settings from "${file.name}"?\n\n`
      + `Source device: ${fromDevice}\n`
      + `Exported: ${fromDate}\n\n`
      + `Sync-classified settings (preferences, telemetry) will be applied.\n`
      + `Local settings (paths, ports) will be kept unchanged.\n`
      + (data.sites?.length ? `${data.sites.length} site(s) will be merged by domain.` : ''),
      'Import settings',
      { confirmButtonText: 'Import', type: 'warning' },
    )

    // Use the same merge logic as pullFromCloud — sync fields only
    const merged: Record<string, string> = {}
    for (const [key, value] of Object.entries(data.settings as Record<string, string>)) {
      if (isSettingSyncable(key)) merged[key] = value
    }
    if (Object.keys(merged).length > 0) {
      await saveSettings(merged)
    }
    ElMessage.success(`Imported ${Object.keys(merged).length} sync settings from ${file.name}`)
    await loadSettings()
  } catch (e) {
    if (e !== 'cancel') ElMessage.error(`Import failed: ${errorMessage(e)}`)
  }
  if (importFileInput.value) importFileInput.value.value = ''
}

// ── Update check ──────────────────────────────────────────────────────
const currentVersion = appVersion

const updateCheck = reactive<{
  loading: boolean
  downloading: boolean
  latest: string | null
  hasUpdate: boolean
  downloadUrl: string | null
  lastCheckedIso: string | null
  error: string | null
  // Task 06: release-notes markdown body from GitHub for inline
  // display so the user sees "what changes before clicking install.
  releaseNotes: string | null
  releaseUrl: string | null
  // Task 06: download progress (from electron-updater download-progress
  // IPC). 0–100 when actively downloading, null otherwise.
  progressPercent: number | null
  progressBytes: string | null
}>({
  loading: false,
  downloading: false,
  latest: null,
  hasUpdate: false,
  downloadUrl: null,
  lastCheckedIso: localStorage.getItem('wdc-last-update-check'),
  error: null,
  releaseNotes: null,
  releaseUrl: null,
  progressPercent: null,
  progressBytes: null,
})

async function runUpdateCheck() {
  updateCheck.loading = true
  updateCheck.error = null
  try {
    const r = await fetch('https://api.github.com/repos/nks-hub/webdev-console/releases/latest')
    if (!r.ok) throw new Error(`GitHub API ${r.status}`)
    const data = await r.json() as {
      tag_name?: string
      html_url?: string
      body?: string
      assets?: Array<{ browser_download_url: string; name: string }>
    }
    const latest = (data.tag_name ?? '').replace(/^v/, '')
    updateCheck.latest = latest
    updateCheck.hasUpdate = compareSemver(latest, currentVersion) > 0
    const setupAsset = (data.assets ?? []).find(a => /setup.*\.exe$/i.test(a.name))
    updateCheck.downloadUrl = setupAsset?.browser_download_url ?? data.html_url ?? null
    updateCheck.releaseNotes = data.body ?? null
    updateCheck.releaseUrl = data.html_url ?? null
    updateCheck.lastCheckedIso = new Date().toISOString()
    localStorage.setItem('wdc-last-update-check', updateCheck.lastCheckedIso)
  } catch (e) {
    updateCheck.error = errorMessage(e)
  } finally {
    updateCheck.loading = false
  }
}

// Task 06: minimal safe markdown-to-HTML for GitHub release notes.
// Intentionally conservative — only handles headings, bold/italic, code
// spans, and bullet lists. Anything fancier should link out via the
// "View on GitHub" action. Escapes HTML first to avoid XSS from arbitrary
// release body text.
function renderReleaseNotes(md: string): string {
  if (!md) return ''
  const esc = md
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
  return esc
    .replace(/^### (.+)$/gm, '<h4>$1</h4>')
    .replace(/^## (.+)$/gm, '<h3>$1</h3>')
    .replace(/^# (.+)$/gm, '<h2>$1</h2>')
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/\*(.+?)\*/g, '<em>$1</em>')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/^- (.+)$/gm, '<li>$1</li>')
    .replace(/(<li>.*<\/li>\n?)+/g, m => '<ul>' + m + '</ul>')
    .replace(/\n\n/g, '<br/><br/>')
}

async function downloadAndInstall() {
  if (!updateCheck.downloadUrl) return
  updateCheck.downloading = true
  try {
    if (window.electronAPI?.openExternal) window.electronAPI.openExternal(updateCheck.downloadUrl)
    else window.open(updateCheck.downloadUrl, '_blank')
    ElMessage.info(t('settings.update.downloadStarted'))
  } finally {
    updateCheck.downloading = false
  }
}

function formatRelativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime()
  const min = Math.floor(diff / 60_000)
  if (min < 1) return t('common.justNow') || 'právě teď'
  if (min < 60) return `${min} min`
  const h = Math.floor(min / 60)
  if (h < 24) return `${h} h`
  return `${Math.floor(h / 24)} d`
}

onMounted(async () => {
  void loadSettings()
  void loadDatabases()
  void loadPhpVersions()
  void loadBackups()
  void loadDeviceId()
  void loadPluginCatalogStatus()
  void loadPluginPorts()
  void loadMysqlRootStatus()
  void loadSnapshots()
  if (accountToken.value) {
    void loadDevicesAccount()
    // Mirror the renderer's accountToken into daemon SettingsStore so
    // CatalogHeartbeatService can see it. Needed for sessions where the
    // token was set in a previous Electron run (stored in localStorage)
    // and the daemon was restarted since — its SettingsStore never saw
    // the login. Silent fail — if daemon is unreachable we'll retry on
    // next Settings page mount.
    void (async () => {
      try {
        await saveSettings({ 'sync.accountToken': accountToken.value })
        // Kick off an immediate device push so the cloud admin UI shows
        // this device as online without waiting for the 60 s heartbeat.
        void autoRegisterDeviceAfterLogin()
      } catch { /* no-op */ }
    })()
  }
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/system`, { headers: authHeaders() })
    if (r.ok) systemInfo.value = await r.json()
  } catch { /* not connected */ }
  // Load installed binary versions for the About tab
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/binaries/installed`, { headers: authHeaders() })
    if (r.ok) {
      const bins: Array<{ app: string; version: string }> = await r.json()
      const seen = new Set<string>()
      installedVersions.value = bins.filter(b => {
        if (seen.has(b.app)) return false
        seen.add(b.app)
        return true
      })
    }
  } catch { /* optional */ }
})

async function save() {
  saving.value = true
  try {
    const payload: Record<string, string> = {
      'ports.http':          String(ports.http),
      'ports.https':         String(ports.https),
      'ports.mysql':         String(ports.mysql),
      'ports.redis':         String(ports.redis),
      'ports.mailpitSmtp':   String(ports.mailpitSmtp),
      'ports.mailpitHttp':   String(ports.mailpitHttp),
      'general.runOnStartup': String(runOnStartup.value),
      'paths.apache':   paths.apache,
      'paths.mysql':    paths.mysql,
      'paths.php':      paths.php,
      'paths.redis':    paths.redis,
      'paths.sitesDir': paths.sitesDir,
      'paths.hostsFile': paths.hostsFile,
      'ports.phpFpmBase': String(phpFpmBasePort.value),
      'daemon.catalogUrl': catalogUrl.value,
      'plugins.autoSyncEnabled': String(pluginAutoSync.value),
      'telemetry.enabled': String(telemetryEnabled.value),
      'telemetry.crashReports': String(telemetryCrashReports.value),
      'backup.dir': backupDir.value,
      'backup.scheduleHours': String(backupScheduleHours.value),
    }
    // saveSettings throws on non-ok with the daemon's error message extracted,
    // giving better feedback than the previous raw-HTTP-status fallback.
    await saveSettings(payload)
    ElMessage.success('Settings saved')
  } catch (e) {
    ElMessage.error(`Failed to save: ${errorMessage(e)}`)
  } finally {
    saving.value = false
  }
}
</script>

<style scoped>
.settings-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 24px 24px 0;
  margin-bottom: 20px;
}

.page-title {
  font-size: 1.25rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.page-subtitle {
  font-size: 0.82rem;
  color: var(--wdc-text-2);
  margin-top: 2px;
}

.page-body {
  padding: 0 24px 24px;
}

.settings-tabs {
  --el-tabs-header-height: 40px;
}

.tab-content {
  padding: 20px 0;
}

.tab-desc {
  font-size: 0.82rem;
  color: var(--el-text-color-secondary);
  margin-bottom: 20px;
  line-height: 1.5;
}

.mono-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
}

.hint {
  margin-top: 8px;
  font-size: 0.76rem;
  color: var(--wdc-text-3);
  line-height: 1.5;
}
.danger-zone {
  margin-top: 32px;
  padding: 18px 20px;
  background: color-mix(in srgb, var(--el-color-danger) 8%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-danger) 30%, transparent);
  border-radius: 8px;
  max-width: 720px;
}
.danger-title {
  margin: 0 0 4px 0;
  font-size: 0.95rem;
  font-weight: 700;
  color: var(--el-color-danger);
}
.danger-desc {
  margin: 0 0 18px 0;
  font-size: 0.82rem;
  color: var(--wdc-text-3);
}
.danger-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 12px 0;
  border-top: 1px solid color-mix(in srgb, var(--el-color-danger) 20%, transparent);
}
.danger-row .danger-info {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.danger-row strong {
  font-size: 0.88rem;
  color: var(--wdc-text-1);
  font-weight: 600;
}
.danger-row .hint {
  font-size: 0.76rem;
  color: var(--wdc-text-3);
  line-height: 1.4;
  max-width: 440px;
}
.hint code {
  font-family: 'JetBrains Mono', monospace;
  background: var(--wdc-surface-2);
  padding: 1px 6px;
  border-radius: 3px;
  color: var(--wdc-accent);
  font-size: 0.74rem;
}
.status-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  margin-right: 6px;
  vertical-align: middle;
}
.status-dot.ok { background: var(--wdc-status-running); }
.status-dot.err { background: var(--wdc-status-error); }

.settings-footer {
  margin-top: 24px;
  padding-top: 16px;
  border-top: 1px solid var(--el-border-color);
  display: flex;
  gap: 8px;
}

/* About tab layout v2 — drops the giant bordered box, uses a two-column
   grid so repos/stack sit next to runtime info instead of stacking with
   wasted space. At narrow widths the grid collapses to a single column
   without any fixed max-width clipping. */
.about-card {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1.2fr);
  gap: 28px 36px;
  padding: 8px 4px;
}
@media (max-width: 820px) {
  .about-card { grid-template-columns: minmax(0, 1fr); gap: 20px; }
}

/* First column — identity block: logo + version + desc + repos + stack. */
.about-card > .about-logo,
.about-card > .about-version,
.about-card > .about-subtitle,
.about-card > .about-desc,
.about-card > .about-links,
.about-card > .about-stack,
.about-card > .about-sso {
  grid-column: 1;
}
/* Second column — system runtime block. */
.about-card > .about-system {
  grid-column: 2;
  grid-row: 1 / span 7;
  margin: 0;
  padding: 0;
  border: none;
}
@media (max-width: 820px) {
  .about-card > .about-system { grid-column: 1; grid-row: auto; }
}

.about-logo {
  display: inline-flex;
  align-items: baseline;
  gap: 12px;
  font-size: 1.4rem;
  font-weight: 800;
  letter-spacing: 0.04em;
  background: linear-gradient(135deg, #6366f1, #8b5cf6);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}
.about-logo::after {
  content: attr(data-version);
  font-size: 0.78rem;
  font-family: 'JetBrains Mono', monospace;
  -webkit-text-fill-color: var(--el-text-color-secondary);
  color: var(--el-text-color-secondary);
  font-weight: 500;
  letter-spacing: 0;
}

.about-version {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.78rem;
  color: var(--el-text-color-secondary);
  margin-top: -4px;
}

.about-subtitle { font-size: 0.88rem; font-weight: 600; color: var(--el-text-color-primary); }
.about-desc { font-size: 0.82rem; color: var(--el-text-color-secondary); line-height: 1.55; max-width: 56ch; }

.about-stack {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
  margin-top: 2px;
}

.about-system { font-size: 0.85rem; }
.about-sys-title {
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
  margin: 0 0 6px;
}
.about-sys-row {
  display: flex;
  justify-content: space-between;
  padding: 3px 0;
  font-size: 0.82rem;
  border-bottom: 1px dashed var(--wdc-border);
}
.about-sys-row:last-child { border-bottom: none; }
.sys-label { color: var(--wdc-text-2); }
.sys-value { color: var(--wdc-text); font-family: 'JetBrains Mono', monospace; font-size: 0.8rem; }

.about-links { display: flex; flex-wrap: wrap; gap: 4px 14px; }
.about-link { color: var(--wdc-accent); text-decoration: none; font-size: 0.82rem; }
.about-link:hover { text-decoration: underline; }

.about-sso { display: flex; flex-direction: column; gap: 6px; padding: 8px 0 0; }

.mysql-root-row {
  display: flex;
  align-items: stretch;
  gap: 8px;
  width: 100%;
}
.mysql-root-row .el-input {
  flex: 1 1 0;
  min-width: 0;
}
.about-sso-status { display: inline-flex; align-items: center; gap: 6px; font-size: 0.78rem; color: var(--el-text-color-secondary); }

.db-list { margin-bottom: 16px; }
.db-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  border-bottom: 1px solid var(--wdc-border);
}
.db-row:last-child { border-bottom: none; }
.db-name { font-family: 'JetBrains Mono', monospace; font-size: 0.88rem; color: var(--wdc-text); }
.db-create { display: flex; gap: 8px; margin-top: 12px; }

/* Sync tab */
.settings-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  margin-bottom: 16px;
  overflow: hidden;
}
.settings-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  background: var(--wdc-surface-2);
  border-bottom: 1px solid var(--wdc-border);
}
.settings-card-title {
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text);
}
.settings-card-body { padding: 18px; }
.sync-actions { display: flex; gap: 8px; flex-wrap: wrap; }
.sync-badge {
  font-size: 0.72rem;
  font-weight: 600;
  padding: 2px 10px;
  border-radius: 10px;
}
.sync-ok { background: rgba(34, 197, 94, 0.15); color: var(--wdc-status-running); }
.sync-err { background: rgba(255, 107, 107, 0.15); color: var(--wdc-status-error); }

.device-name-cell { display: flex; align-items: center; gap: 6px; }
.device-name-text { cursor: pointer; }
.device-name-text:hover { text-decoration: underline dashed var(--wdc-text-3); text-underline-offset: 3px; }
.device-name-input { max-width: 160px; }

/* Update tab */
.mono { font-family: 'JetBrains Mono', monospace; font-size: 0.88rem; }
.text-muted { color: var(--wdc-text-3); font-size: 0.82rem; }
.update-actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; margin-top: 16px; }
</style>
