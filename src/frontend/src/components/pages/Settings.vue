<template>
  <div class="settings-page">
    <div class="page-header">
      <div>
        <h1 class="page-title">{{ $t('settings.title') }}</h1>
        <p class="page-subtitle">{{ $t('settings.subtitle') }}</p>
      </div>
    </div>

    <div class="page-body">
      <div v-if="uiModeStore.isSimple" class="simple-settings-grid">
        <section class="simple-settings-panel simple-settings-panel-main">
          <header class="simple-settings-panel-header">
            <div>
              <h2>{{ $t('settings.tabs.general') }}</h2>
              <p>{{ $t('settings.general.tabDesc') }}</p>
            </div>
          </header>
          <EasyGeneralSettings
            :t="t"
            :locale="locale"
            :theme-mode="themeStore.mode"
            :is-advanced="uiModeStore.isAdvanced"
            :run-on-startup="runOnStartup"
            :default-php="defaultPhp"
            :php-versions="phpVersions"
            :flushing-dns="flushingDns"
            :mamp-discovering="mampDiscovering"
            :telemetry-enabled="telemetryEnabled"
            :telemetry-crash-reports="telemetryCrashReports"
            :standalone="false"
            @update:locale="onLocaleChange"
            @update:theme-mode="themeStore.setMode"
            @update:ui-mode="uiModeStore.setUiMode"
            @update:run-on-startup="runOnStartup = $event"
            @update:default-php="defaultPhp = $event"
            @update:telemetry-enabled="telemetryEnabled = $event"
            @update:telemetry-crash-reports="telemetryCrashReports = $event"
            @flush-dns="flushDns"
            @discover-mamp="discoverMamp"
          />
        </section>

        <section class="simple-settings-panel">
          <header class="simple-settings-panel-header">
            <div>
              <h2>{{ $t('settings.tabs.update') }}</h2>
              <p>{{ $t('settings.update.notChecked') }}</p>
            </div>
          </header>
          <EasyUpdateSettings
            :t="t"
            :current-version="currentVersion"
            :update-check="updateCheck"
            :format-relative-time="formatRelativeTime"
            :render-release-notes="renderReleaseNotes"
            @check="runUpdateCheck"
            @download="downloadAndInstall"
          />
        </section>

        <!-- Simple Network panel — read-only port snapshot + DNS flush
             shortcut. The full per-plugin port editor stays in Advanced;
             beginners just need to see "what listens where" without
             leaving Simple. -->
        <section class="simple-settings-panel">
          <header class="simple-settings-panel-header">
            <div>
              <h2>{{ $t('settings.simple.network.title') }}</h2>
              <p>{{ $t('settings.simple.network.subtitle') }}</p>
            </div>
          </header>
          <div class="simple-runtime-grid">
            <div class="about-sys-row">
              <span class="sys-label">Apache HTTP</span>
              <span class="sys-value mono">:{{ httpPort }}</span>
            </div>
            <div class="about-sys-row">
              <span class="sys-label">Apache HTTPS</span>
              <span class="sys-value mono">:{{ httpsPort }}</span>
            </div>
            <div class="about-sys-row">
              <span class="sys-label">MySQL</span>
              <span class="sys-value mono">:{{ mysqlPort }}</span>
            </div>
          </div>
          <div class="simple-settings-actions">
            <el-button size="small" @click="uiModeStore.setUiMode('advanced')">
              {{ $t('settings.simple.network.editInAdvanced') }} →
            </el-button>
          </div>
        </section>

        <!-- Simple Backup panel — one-click "create a backup now". The
             retention + scheduler controls live in Advanced; Simple just
             surfaces the safety net. -->
        <section class="simple-settings-panel">
          <header class="simple-settings-panel-header">
            <div>
              <h2>{{ $t('settings.simple.backup.title') }}</h2>
              <p>{{ $t('settings.simple.backup.subtitle') }}</p>
            </div>
          </header>
          <div class="simple-settings-actions">
            <el-button
              type="primary"
              size="small"
              :loading="creatingBackup"
              @click="createBackupFromSimple"
            >
              {{ $t('settings.simple.backup.create') }}
            </el-button>
            <el-button size="small" @click="$router.push('/backups')">
              {{ $t('settings.simple.backup.openManager') }} →
            </el-button>
          </div>
        </section>

        <section v-if="systemInfo" class="simple-settings-panel simple-runtime-panel">
          <header class="simple-settings-panel-header">
            <div>
              <h2>{{ $t('settings.tabs.about') }}</h2>
              <p>NKS WDC v{{ appVersion }}</p>
            </div>
          </header>
          <div class="simple-runtime-grid">
            <div class="about-sys-row">
              <span class="sys-label">{{ $t('settings.about.services') }}</span>
              <span class="sys-value">{{ systemInfo.services?.running }}/{{ systemInfo.services?.total }}</span>
            </div>
            <div class="about-sys-row">
              <span class="sys-label">{{ $t('settings.about.sites') }}</span>
              <span class="sys-value">{{ systemInfo.sites }}</span>
            </div>
            <div v-if="systemInfo.daemon?.version" class="about-sys-row">
              <span class="sys-label">{{ $t('settings.about.daemonVersion') }}</span>
              <span class="sys-value mono">{{ systemInfo.daemon.version }}</span>
            </div>
            <div v-if="systemInfo.daemon?.uptime !== undefined" class="about-sys-row">
              <span class="sys-label">{{ $t('settings.about.daemonUptime') }}</span>
              <span class="sys-value">{{ formatUptime(systemInfo.daemon.uptime) }}</span>
            </div>
          </div>
        </section>

        <div class="settings-footer simple-settings-footer">
          <el-button type="primary" size="small" :loading="saving" @click="save">
            {{ $t('common.save') }} {{ $t('common.settings') }}
          </el-button>
          <el-button size="small" @click="loadSettings">{{ $t('common.reset') }}</el-button>
        </div>
      </div>

      <el-tabs v-else v-model="activeTab" class="settings-tabs">
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
            <!-- Phase 6.21 — explain what changing the webserver port
                 actually does, since the consequence isn't obvious from
                 the form alone. The daemon now bulk-regenerates every
                 site's vhost on Apache port change (Phase 6.20a) AND
                 self-heals stale ports on boot (Phase 6.20b), but the
                 user still sees a brief window where the webserver
                 reloads and existing browser connections drop. -->
            <el-alert
              type="info"
              :closable="false"
              show-icon
              style="margin-bottom: 12px; max-width: 400px"
            >
              <template #title>Changing HTTP/HTTPS port reloads the webserver</template>
              Every per-site vhost is regenerated to use the new port and
              Apache (or nginx/caddy) is reloaded. In-flight browser
              connections drop briefly. Check that the new port isn't
              already used by another service before saving.
            </el-alert>
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
          <EasyGeneralSettings
            :t="t"
            :locale="locale"
            :theme-mode="themeStore.mode"
            :is-advanced="uiModeStore.isAdvanced"
            :run-on-startup="runOnStartup"
            :default-php="defaultPhp"
            :php-versions="phpVersions"
            :flushing-dns="flushingDns"
            :mamp-discovering="mampDiscovering"
            :telemetry-enabled="telemetryEnabled"
            :telemetry-crash-reports="telemetryCrashReports"
            @update:locale="onLocaleChange"
            @update:theme-mode="themeStore.setMode"
            @update:ui-mode="uiModeStore.setUiMode"
            @update:run-on-startup="runOnStartup = $event"
            @update:default-php="defaultPhp = $event"
            @update:telemetry-enabled="telemetryEnabled = $event"
            @update:telemetry-crash-reports="telemetryCrashReports = $event"
            @flush-dns="flushDns"
            @discover-mamp="discoverMamp"
          />
        </el-tab-pane>
        <!-- Paths tab -->
        <el-tab-pane v-if="uiModeStore.isAdvanced" :label="$t('settings.tabs.paths')" name="paths">
          <div class="tab-content">
            <p class="tab-desc">{{ $t('settings.paths.tabDesc') }}</p>
            <!-- F79: Browse buttons open the native file/folder dialog via
                 electronAPI.showOpenDialog. Falls back to manual typing when
                 running outside Electron (dev browser, etc.). -->
            <el-form label-position="top" size="small" style="max-width: 560px">
              <el-form-item :label="$t('settings.paths.apache')">
                <el-input v-model="paths.apache" placeholder="C:\nks-wdc\binaries\apache\2.4\bin\httpd.exe">
                  <template #append>
                    <el-button @click="browsePath('apache', 'file')">{{ $t('settings.paths.browse') }}</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item :label="$t('settings.paths.mysql')">
                <el-input v-model="paths.mysql" placeholder="C:\nks-wdc\binaries\mysql\8.0\bin\mysqld.exe">
                  <template #append>
                    <el-button @click="browsePath('mysql', 'file')">{{ $t('settings.paths.browse') }}</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item :label="$t('settings.paths.php')">
                <el-input v-model="paths.php" placeholder="C:\nks-wdc\binaries\php\8.4\php.exe">
                  <template #append>
                    <el-button @click="browsePath('php', 'file')">{{ $t('settings.paths.browse') }}</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item :label="$t('settings.paths.redis')">
                <el-input v-model="paths.redis" placeholder="C:\nks-wdc\binaries\redis\7.2\redis-server.exe">
                  <template #append>
                    <el-button @click="browsePath('redis', 'file')">{{ $t('settings.paths.browse') }}</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item :label="$t('settings.paths.sitesDir')">
                <el-input v-model="paths.sitesDir" placeholder="C:\nks-wdc\conf\vhosts">
                  <template #append>
                    <el-button @click="browsePath('sitesDir', 'folder')">{{ $t('settings.paths.browse') }}</el-button>
                  </template>
                </el-input>
              </el-form-item>
              <el-form-item :label="$t('settings.paths.hostsFile')">
                <el-input v-model="paths.hostsFile" placeholder="C:\Windows\System32\drivers\etc\hosts">
                  <template #append>
                    <el-button @click="browsePath('hostsFile', 'file')">{{ $t('settings.paths.browse') }}</el-button>
                  </template>
                </el-input>
                <div class="hint">{{ $t('settings.paths.hostsHint') }}</div>
              </el-form-item>

              <el-divider />

              <el-form-item :label="$t('settings.paths.dataDir')">
                <el-input
                  :model-value="systemInfo?.os?.machine ? `${systemInfo?.daemon?.pid ? '~/.wdc' : '~/.wdc'}` : '~/.wdc'"
                  disabled
                  class="mono-input"
                />
                <div class="hint">
                  {{ $t('settings.paths.dataHint') }}
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
            <p class="tab-desc">{{ $t('settings.advanced.tabDesc') }}</p>
            <el-form label-position="top" size="small" style="max-width: 560px">
              <el-form-item :label="$t('settings.advanced.catalogUrl')">
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
              <el-form-item :label="$t('settings.advanced.binaryReleases')">
                <el-button size="small" @click="testCatalogReachable" :loading="testingCatalog">
                  {{ $t('settings.advanced.testConnection') }}
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
              <el-form-item :label="$t('settings.advanced.mysqlRoot')">
                <div class="mysql-root-row">
                  <el-input
                    v-model="mysqlRootPassword"
                    type="password"
                    show-password
                    :placeholder="mysqlRootExists ? $t('settings.advanced.mysqlRootPlaceholderStored') : $t('settings.advanced.mysqlRootPlaceholderEnter')"
                    class="mono-input"
                  />
                  <el-button
                    size="small"
                    :loading="mysqlRootSaving"
                    :disabled="!mysqlRootPassword"
                    @click="saveMysqlRootPassword"
                  >{{ $t('settings.advanced.save') }}</el-button>
                </div>
                <div class="hint">
                  {{ mysqlRootExists ? 'A password is currently stored (encrypted via DPAPI).' : 'No password stored — WDC cannot authenticate to mysqld.' }}
                  Use this field when your mysqld root password was set outside WDC (external MySQL install, MAMP import, or manual change) — WDC only syncs its stored copy, you still need to run
                  <code>ALTER USER 'root'@'localhost' IDENTIFIED BY '…'</code>
                  on the server itself.
                </div>
              </el-form-item>
              <el-form-item :label="$t('settings.advanced.pluginAutoSync')">
                <el-switch v-model="pluginAutoSync" />
                <el-button
                  size="small"
                  style="margin-left: 12px"
                  :loading="syncingPlugins"
                  @click="syncPluginsNow"
                >{{ $t('settings.advanced.syncNow') }}</el-button>
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

            <!-- Phase 6.23 — MCP integration toggle. Default OFF: hides
                 the AI agent confirmation banner + MCP Intents sidebar
                 entry + makes the daemon's /api/mcp/intents endpoints
                 return 404. Operators not running an AI client see no
                 trace of the subsystem. -->
            <div class="settings-section" id="mcp-section" style="margin-top: 16px">
              <h4 class="section-title">{{ $t('settings.mcp.title') }}</h4>
              <p class="hint">
                {{ $t('settings.mcp.description') }}
                <strong>{{ $t('settings.mcp.warning') }}</strong>
              </p>
              <el-form label-position="left" label-width="200px" size="small" style="max-width: 480px">
                <el-form-item :label="$t('settings.mcp.enableLabel')">
                  <el-switch v-model="mcpEnabled" />
                </el-form-item>
                <!-- Phase 7.4e — strict kind validation. Default OFF (lenient
                     mode where any regex-valid kind passes). Turn ON to refuse
                     any kind not registered via IDestructiveOperationKinds. -->
                <el-form-item v-if="mcpEnabled" :label="$t('settings.mcp.strictKindsLabel')">
                  <el-switch v-model="mcpStrictKinds" />
                  <div class="hint" style="margin-top: 4px">{{ $t('settings.mcp.strictKindsHint') }}</div>
                </el-form-item>
                <!-- Phase 7.5+++ — always-confirm kinds override. Comma-
                     separated list (e.g. "restore,cancel") of kind ids
                     for which the validator skips grant auto-approval.
                     Operator's "ring-fence the riskiest ops" knob: even
                     wildcard always-grants must yield to the GUI banner
                     for these kinds. -->
                <el-form-item v-if="mcpEnabled" :label="$t('settings.mcp.alwaysConfirmKindsLabel')">
                  <!-- Phase 7.5+++ — multi-select over registered kinds.
                       Falls back to a free-text input only if the kinds
                       endpoint hasn't responded yet (or returns empty),
                       so the setting is still editable without the
                       registry. The store/save format stays a comma-
                       separated string (back-compat with backend). -->
                  <el-select
                    v-if="mcpKindOptions.length > 0"
                    v-model="mcpAlwaysConfirmKindsArr"
                    multiple
                    filterable
                    collapse-tags
                    collapse-tags-tooltip
                    :placeholder="$t('settings.mcp.alwaysConfirmKindsPlaceholder')"
                    style="max-width: 480px; width: 100%"
                  >
                    <el-option
                      v-for="opt in mcpKindOptions"
                      :key="opt.id"
                      :label="humanKindLabel(opt)"
                      :value="opt.id"
                    >
                      <span>{{ humanKindLabel(opt) }}</span>
                      <span class="muted" style="margin-left: 8px; font-size: 11px">
                        {{ opt.id }} · {{ $t('mcpKinds.danger.' + opt.danger) }}
                      </span>
                    </el-option>
                  </el-select>
                  <el-input
                    v-else
                    v-model="mcpAlwaysConfirmKinds"
                    placeholder="restore,cancel"
                    style="max-width: 320px"
                  />
                  <!-- Phase 7.5+++ — one-click presets. "Lock all destructive"
                       fills the picker with every kind tagged Destructive
                       (restore, test_hook, settings_write, plus any plugin-
                       contributed dangerous kinds). "Clear" resets. -->
                  <div v-if="mcpKindOptions.length > 0" style="margin-top: 6px">
                    <el-button
                      size="small"
                      :disabled="destructiveKindIds.length === 0 || allDestructiveAlreadyLocked"
                      @click="lockAllDestructive"
                    >
                      🔒 {{ $t('settings.mcp.lockAllDestructive', { n: destructiveKindIds.length }) }}
                    </el-button>
                    <el-button
                      size="small"
                      plain
                      :disabled="mcpAlwaysConfirmKindsArr.length === 0"
                      @click="clearAlwaysConfirm"
                    >
                      {{ $t('settings.mcp.clearAlwaysConfirm') }}
                    </el-button>
                  </div>
                  <div class="hint" style="margin-top: 4px">{{ $t('settings.mcp.alwaysConfirmKindsHint') }}</div>
                </el-form-item>
                <!-- Phase 7.5+++ — operator-tunable janitor retention.
                     Defaults match GrantSweeperService.Default* (1d/30d).
                     Setting either to 0 disables that branch (keep all). -->
                <el-form-item v-if="mcpEnabled" :label="$t('settings.mcp.expiredRetentionLabel')">
                  <el-input-number v-model="mcpExpiredRetentionDays"
                    :min="0" :max="365" controls-position="right" style="width: 120px" />
                  <span class="hint" style="margin-left: 8px">{{ $t('settings.mcp.daysSuffix') }}</span>
                  <div class="hint" style="margin-top: 4px">{{ $t('settings.mcp.expiredRetentionHint') }}</div>
                </el-form-item>
                <el-form-item v-if="mcpEnabled" :label="$t('settings.mcp.revokedRetentionLabel')">
                  <el-input-number v-model="mcpRevokedRetentionDays"
                    :min="0" :max="365" controls-position="right" style="width: 120px" />
                  <span class="hint" style="margin-left: 8px">{{ $t('settings.mcp.daysSuffix') }}</span>
                  <div class="hint" style="margin-top: 4px">{{ $t('settings.mcp.revokedRetentionHint') }}</div>
                </el-form-item>
                <!-- Phase 8 — mcp_tool_calls audit retention. Drives the
                     hourly McpToolCallsSweeperService. Bumping this
                     keeps history for forensics; lowering reclaims disk
                     when traffic is heavy. -->
                <el-form-item v-if="mcpEnabled" :label="$t('settings.mcp.toolCallRetentionLabel')">
                  <el-input-number v-model="mcpToolCallRetentionDays"
                    :min="1" :max="365" controls-position="right" style="width: 120px" />
                  <span class="hint" style="margin-left: 8px">{{ $t('settings.mcp.daysSuffix') }}</span>
                  <div class="hint" style="margin-top: 4px">{{ $t('settings.mcp.toolCallRetentionHint') }}</div>
                </el-form-item>
              </el-form>
            </div>

            <!-- Phase 7.1a — deploy subsystem toggle. Default ON since
                 most users install WDC FOR site management with deploy.
                 OFF hides Deploy tab in SiteEdit + plugin REST endpoints
                 return 404. Useful for installs that only use WDC as
                 local Apache/MySQL manager without remote deploys. -->
            <div id="deploy-subsystem" class="settings-section" style="margin-top: 16px">
              <h4 class="section-title">{{ $t('settings.deploySubsystem.title') }}</h4>
              <p class="hint">
                {{ $t('settings.deploySubsystem.description') }}
              </p>
              <!-- Iter 13: backend mode status banner. Shows operator at a
                   glance which deploy backend is currently authoritative,
                   sourced from /api/admin/plugin-readiness. Today always
                   built-in (Program.cs handlers), but the banner already
                   reflects the live mode so a future flip is visible
                   without leaving Settings. -->
              <div
                v-if="deployBackendMode"
                class="hint"
                style="margin-bottom: 12px; padding: 10px 14px; border-radius: var(--wdc-radius-sm); background: var(--wdc-surface-2); border: 1px solid var(--wdc-border); display: inline-block"
              >
                <span v-if="deployBackendMode === 'plugin'" style="color: var(--el-color-success)">
                  🔌 {{ $t('settings.deploySubsystem.modePlugin', { v: deployPluginVersion ?? '?' }) }}
                </span>
                <span v-else style="color: var(--el-color-info)">
                  ⚙ {{ $t('settings.deploySubsystem.modeBuiltIn') }}
                </span>
                <span v-if="deployPluginLoaded && deployBackendMode === 'built-in'" class="muted" style="margin-left: 8px">
                  ({{ $t('settings.deploySubsystem.pluginLoadedNotActive', { v: deployPluginVersion ?? '?' }) }})
                </span>
              </div>
              <el-form label-position="left" label-width="200px" size="small" style="max-width: 480px">
                <el-form-item :label="$t('settings.deploySubsystem.enableLabel')">
                  <el-switch v-model="deployEnabled" />
                </el-form-item>
                <!-- Phase 7.4 #109-D1 — backend selector. Today the toggle
                     is INFORMATIONAL only: useLegacyHostHandlers=true means
                     Program.cs deploy handlers serve everything (current
                     behaviour). The switch is disabled with a tooltip
                     explaining the future flip needs plugin parity.
                     Persisted setting round-trips through Settings save
                     so when phase B/C land + tools/e2e proves parity,
                     operators can flip without code changes. -->
                <!-- Iter 58 (#258) — restart-pending banner: when the
                     operator flipped this switch but the daemon hasn't
                     restarted, the conditional handler registration still
                     honours the boot value. Banner appears INLINE so the
                     operator sees it the moment they save without having
                     to open the per-site popover. -->
                <el-alert
                  v-if="deployRestartPending"
                  :title="$t('deploySettings.restartPendingTitle')"
                  :description="$t('deploySettings.restartPendingDescription')"
                  type="warning"
                  :closable="false"
                  show-icon
                  style="margin-bottom: 12px; max-width: 480px"
                />
                <el-form-item :label="$t('settings.deploySubsystem.legacyHandlersLabel')">
                  <el-tooltip
                    :content="$t('settings.deploySubsystem.legacyHandlersTooltip')"
                    placement="right"
                  >
                    <el-switch v-model="deployUseLegacyHostHandlers" :disabled="!deployFlipUnlocked && !deployRestartPending" />
                  </el-tooltip>
                  <span class="hint" style="margin-left: 12px">
                    {{ $t('settings.deploySubsystem.legacyHandlersHint') }}
                  </span>
                  <el-popover
                    v-if="!deployFlipUnlocked && deployFlipBlockers.length > 0"
                    placement="right"
                    :width="400"
                    trigger="click"
                  >
                    <template #reference>
                      <el-tag
                        type="warning"
                        size="small"
                        effect="plain"
                        style="margin-left: 12px; cursor: pointer"
                      >
                        🔒 {{ $t('settings.deploySubsystem.legacyHandlersLocked', { n: deployFlipBlockers.length }) }}
                      </el-tag>
                    </template>
                    <!-- Iter 21: shared component renders blockerDetails
                         phase tag + remediation. Same shape as the per-site
                         DeploySettingsPanel popover so operator sees identical
                         UI in both global + per-site contexts.
                         Iter 24: localized recommendation header mirrors the
                         per-site popover for visual symmetry. -->
                    <div style="font-size: 12px">
                      <div style="margin-bottom: 6px">
                        <strong>
                          {{ deployFlipUnlocked
                            ? $t('deploySettings.readyToFlip')
                            : $t('deploySettings.stayOnBuiltIn', { n: deployFlipBlockers.length }) }}
                        </strong>
                      </div>
                      <ReadinessBlockerList
                        :blockers="deployFlipBlockers"
                        :blocker-details="deployFlipBlockerDetails"
                      />
                      <!-- Iter 64 — mirror DeploySettingsPanel's gatedEndpoints
                           list so the global popover and per-site popover
                           stay visually symmetric. Operator sees the same
                           cutover scope from either entry point. -->
                      <div
                        v-if="deployFlipGatedEndpoints.length > 0"
                        style="margin-top: 10px"
                      >
                        <div class="muted" style="margin-bottom: 4px; font-size: 11px">
                          {{ $t('deploySettings.gatedEndpointsLabel', { n: deployFlipGatedEndpoints.length }) }}
                        </div>
                        <ul style="font-size: 11px; margin: 0; padding-left: 18px; max-height: 140px; overflow-y: auto">
                          <li v-for="ep in deployFlipGatedEndpoints" :key="ep">
                            <code class="mono">{{ ep }}</code>
                          </li>
                        </ul>
                      </div>
                      <div class="muted" style="margin-top: 8px; font-size: 11px">
                        <code class="mono">GET /api/admin/plugin-readiness?explain=true</code>
                      </div>
                    </div>
                  </el-popover>
                </el-form-item>
              </el-form>
            </div>

            <!-- #147 — OS notification toggles. Stored client-side
                 (localStorage via osNotifications service) since they
                 only matter for THIS desktop install. Each channel can
                 be turned off independently so an operator who lives in
                 the WDC window all day can mute deploy toasts but keep
                 the louder MCP confirm requests. -->
            <div class="settings-section" style="margin-top: 16px">
              <h4 class="section-title">{{ $t('settings.osNotify.title') }}</h4>
              <p class="hint">{{ $t('settings.osNotify.description') }}</p>
              <el-form label-position="left" label-width="200px" size="small" style="max-width: 400px">
                <el-form-item :label="$t('settings.osNotify.deployLabel')">
                  <el-switch v-model="osNotifyDeploy" />
                </el-form-item>
                <el-form-item :label="$t('settings.osNotify.mcpLabel')">
                  <el-switch v-model="osNotifyMcp" />
                </el-form-item>
                <el-form-item :label="$t('settings.osNotify.systemLabel')">
                  <el-switch v-model="osNotifySystem" />
                </el-form-item>
                <el-form-item>
                  <el-button size="small" plain @click="onTestOsNotify">
                    {{ $t('settings.osNotify.testBtn') }}
                  </el-button>
                </el-form-item>
              </el-form>
            </div>
          </div>
        </el-tab-pane>

        <!-- Account & Devices tab -->
        <el-tab-pane :label="$t('settings.tabs.account')" name="account">
          <div class="tab-content">
            <!-- F91.4: SSO (catalog-api OIDC) moved from About -> Account
                 because signing in belongs with account management, not
                 with "what version is this" metadata. Shown in both
                 simple + advanced modes so simple users can still sign
                 in to their catalog identity. -->
            <AccountSsoCard
              :t="$t"
              :is-authenticated="authStore.isAuthenticated"
              :display-name="authStore.displayName"
              :login-pending="authStore.loginPending"
              :login-error="authStore.loginError"
              @login="ssoLogin"
              @logout="authStore.logout()"
            />
            <!-- F91.15: password login restored alongside SSO. The two
                 paths write to the same authStore (token + displayName),
                 just through different entry points — SSO card above
                 opens Authentik, password form here hits
                 /api/v1/auth/login directly. "Unified login" = one
                 Account tab hosting both, not one removed. -->
            <template v-if="uiModeStore.isSimple">
              <AccountPasswordCard
                v-if="!accountToken"
                :t="$t"
                :title="$t('settings.tabs.account')"
                v-model:email="authEmail"
                v-model:password="authPassword"
                :loading="authLoading"
                :error="authError"
                @login="doLogin"
                @register="doRegister"
              />
              <AccountSimpleSyncCard
                v-else
                :t="$t"
                :title="$t('settings.tabs.account')"
                :email="accountEmail"
                :syncing="syncing"
                :pulling="pulling"
                @push="pushToCloud"
                @pull="pullFromCloud"
                @logout="doLogout"
              />
            </template>

            <!-- Advanced mode: full account UI. Shows password form when
                 not signed in, device management + push/pull when signed
                 in. SSO card above is the other entry point; both write
                 the same authStore so switching between them is seamless. -->
            <template v-if="!uiModeStore.isSimple">
              <AccountPasswordCard
                v-if="!accountToken"
                :t="$t"
                :title="$t('settings.account.passwordTitle')"
                v-model:email="authEmail"
                v-model:password="authPassword"
                :loading="authLoading"
                :error="authError"
                @login="doLogin"
                @register="doRegister"
              />
              <AccountAdvancedSummaryCard
                v-else
                :t="$t"
                :email="accountEmail"
                :devices-loading="devicesLoading"
                @refresh-devices="loadDevicesAccount"
                @logout="doLogout"
              />


              <!-- F91.15: devices list only when signed in - same gate
                   as the Account summary above. -->
              <AccountDeviceTableCard
                v-if="accountToken"
                :devices="accountDevices"
                v-model:editing-device-name="editingDeviceName"
                v-model:editing-device-value="editingDeviceValue"
                :pushing-to="pushingTo"
                :unlinking-device="unlinkingDevice"
                @start-edit-name="startEditDeviceName"
                @save-name="saveDeviceName"
                @push-config="pushMyConfigTo"
                @unlink="unlinkDevice"
              />
            </template>
          </div>
        </el-tab-pane>

        <!-- Update tab — visible in both Simple and Advanced -->
        <el-tab-pane :label="$t('settings.tabs.update')" name="update">
          <EasyUpdateSettings
            :t="t"
            :current-version="currentVersion"
            :update-check="updateCheck"
            :format-relative-time="formatRelativeTime"
            :render-release-notes="renderReleaseNotes"
            @check="runUpdateCheck"
            @download="downloadAndInstall"
          />
        </el-tab-pane>
        <!-- Sync tab — cloud config sync + export/import -->
        <el-tab-pane v-if="uiModeStore.isAdvanced" :label="$t('settings.tabs.sync')" name="sync">
          <div class="tab-content">
            <p class="tab-desc">{{ $t('settings.sync.topDesc') }}</p>

            <!-- Device identity -->
            <SyncDeviceIdentityCard
              :t="$t"
              :device-id="deviceId"
              v-model:device-name="deviceName"
              @copy="copyDeviceId"
            />

            <!-- Cloud sync -->
            <SyncCloudCard
              :t="$t"
              :sync-status="syncStatus"
              :last-sync-time="lastSyncTime"
              :last-sync-display="lastSyncDisplay"
              :syncing="syncing"
              :pulling="pulling"
              :checking-cloud="checkingCloud"
              :disabled="!catalogUrl && !deviceId"
              @push="pushToCloud"
              @pull="pullFromCloud"
              @check="checkCloudExists"
            />

            <!-- Task 03: Cloud snapshots - recent snapshot list from
                 catalog-api /sync/snapshots with restore/delete actions.
                 Snapshots are auto-created by the cloud BEFORE each push
                 overwrites a device config (see catalog-api task 34). -->
            <SyncSnapshotsCard
              v-if="accountToken"
              :t="$t"
              :snapshots="snapshots"
              :loading="snapshotsLoading"
              :snapshot-action="snapshotAction"
              :format-date="formatDate"
              @refresh="loadSnapshots"
              @restore="restoreSnapshot"
              @delete="deleteSnapshot"
            />
            <!-- File export / import -->
            <SyncExportImportCard
              :t="$t"
              @export="exportSettings"
              @import="importSettings"
            />
          </div>
        </el-tab-pane>

        <!-- About tab -->
        <el-tab-pane :label="$t('settings.tabs.about')" name="about">
          <div class="tab-content">
            <div class="about-card">
              <div class="about-logo">NKS WDC</div>
              <div class="about-version">v{{ appVersion }}</div>
              <div class="about-subtitle">{{ $t('settings.about.subtitle') }}</div>
              <div class="about-desc">{{ $t('settings.about.description') }}</div>

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
                <div class="about-sys-title">{{ $t('settings.about.runtime') }}</div>
                <!-- F85: daemon uptime + PID surfaced so user can see the
                     F90 fix reporting sane values (uptime since daemon start,
                     not system boot). -->
                <div v-if="systemInfo.daemon?.uptime !== undefined" class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.daemonUptime') }}</span>
                  <span class="sys-value">{{ formatUptime(systemInfo.daemon.uptime) }}</span>
                </div>
                <div v-if="systemInfo.daemon?.pid" class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.daemonPid') }}</span>
                  <span class="sys-value mono">{{ systemInfo.daemon.pid }}</span>
                </div>
                <div v-if="systemInfo.daemon?.version" class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.daemonVersion') }}</span>
                  <span class="sys-value mono">{{ systemInfo.daemon.version }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.services') }}</span>
                  <span class="sys-value">{{ systemInfo.services?.running }}/{{ systemInfo.services?.total }} {{ $t('dashboard.running') }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.sites') }}</span>
                  <span class="sys-value">{{ systemInfo.sites }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.plugins') }}</span>
                  <span class="sys-value">{{ systemInfo.plugins }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.binaries') }}</span>
                  <span class="sys-value">{{ systemInfo.binaries }}</span>
                </div>
                <div v-if="installedVersions.length" class="about-sys-title" style="margin-top: 12px">{{ $t('settings.about.installedVersions') }}</div>
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
      <div class="settings-footer" v-if="uiModeStore.isAdvanced && activeTab !== 'about' && activeTab !== 'update'">
        <el-button type="primary" size="small" :loading="saving" @click="save">
          {{ $t('common.save') }} {{ $t('common.settings') }}
        </el-button>
        <el-button size="small" @click="loadSettings">{{ $t('common.reset') }}</el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, onBeforeUnmount, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useThemeStore } from '../../stores/theme'
import { useUiModeStore } from '../../stores/uiMode'
import { useAuthStore } from '../../stores/auth'
import {
  catalogRegister, catalogLogin, fetchDevices, pushConfigToDevice,
  daemonBaseUrl, daemonAuthHeaders as authHeaders,
  fetchPhpVersions, fetchSettings, saveSettings,
  subscribeEventsMap,
  type DeviceInfo as CatalogDeviceInfo,
  type SystemInfo,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { osNotify, isChannelEnabled, setChannelEnabled } from '../../services/osNotifications'
import ReadinessBlockerList from '../deploy/ReadinessBlockerList.vue'
import AccountAdvancedSummaryCard from '../settings/account/AccountAdvancedSummaryCard.vue'
import AccountDeviceTableCard from '../settings/account/AccountDeviceTableCard.vue'
import AccountPasswordCard from '../settings/account/AccountPasswordCard.vue'
import AccountSimpleSyncCard from '../settings/account/AccountSimpleSyncCard.vue'
import AccountSsoCard from '../settings/account/AccountSsoCard.vue'
import EasyGeneralSettings from '../settings/easy/EasyGeneralSettings.vue'
import EasyUpdateSettings from '../settings/easy/EasyUpdateSettings.vue'
import SyncCloudCard from '../settings/sync/SyncCloudCard.vue'
import SyncDeviceIdentityCard from '../settings/sync/SyncDeviceIdentityCard.vue'
import SyncExportImportCard from '../settings/sync/SyncExportImportCard.vue'
import SyncSnapshotsCard from '../settings/sync/SyncSnapshotsCard.vue'
import { compareSemver } from '../../utils/semver'
import { useAppVersion } from '../../utils/appVersion'

const versionRef = useAppVersion()
const appVersion = computed(() => versionRef.value.full)
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
const { locale, t, te } = useI18n()

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

// Phase 7.5+++ — deep-link target. Supports ?tab=advanced&scroll=mcp-section
// from cross-page links (McpKinds always-confirm chip, etc.). Selecting
// the tab is enough to surface the MCP section within Advanced; the
// scroll attribute lets us jump to a specific anchor.
const route = useRoute()
function applyDeepLink(): void {
  const tabParam = typeof route.query.tab === 'string' ? route.query.tab : ''
  if (tabParam) {
    if (uiModeStore.isSimple && ADVANCED_ONLY_TABS.has(tabParam)) return
    activeTab.value = tabParam
  }
  const scrollTarget = typeof route.query.scroll === 'string' ? route.query.scroll : ''
  if (scrollTarget) {
    // Defer to next tick so the tab content is in the DOM before we
    // scroll to its anchor.
    setTimeout(() => {
      const el = document.getElementById(scrollTarget)
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' })
    }, 100)
  }
}
watch(() => route.query, applyDeepLink, { immediate: true })

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
// Phase 6.23 — MCP integration toggle. Default false; mirrors daemon's
// own `mcp.enabled` setting. When false, sidebar entry, banner, and
// /api/mcp/intents endpoints are all hidden/404.
const mcpEnabled = ref(false)
// Phase 7.4e — strict kind validation. Default false (lenient).
// When true, intents with unregistered kinds get kind_unknown.
const mcpStrictKinds = ref(false)
// Phase 7.5+++ — always-confirm kinds. Comma-separated list (e.g.
// "restore,cancel"). Validator skips grant auto-approval for these
// kinds, forcing GUI confirmation even with wildcard always-grants.
const mcpAlwaysConfirmKinds = ref('')
// Two-way bridge between the comma-string storage format and the
// el-select array model. The select renders the options, but the
// backend persists a string for back-compat.
const mcpAlwaysConfirmKindsArr = computed<string[]>({
  get: () => mcpAlwaysConfirmKinds.value
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0),
  set: (arr: string[]) => {
    mcpAlwaysConfirmKinds.value = arr.join(',')
  },
})
// Registered kinds for the multi-select. Fetched lazily; the input
// gracefully falls back to a free-text field while empty.
interface SettingsMcpKindOption {
  id: string
  label?: string
  danger: 'reversible' | 'destructive'
}
const mcpKindOptions = ref<SettingsMcpKindOption[]>([])

// Phase 7.5+++ — preset helpers for the always-confirm picker. The
// "lock all destructive" button is the safest one-click ring-fence:
// any kind tagged DangerLevel.Destructive (restore, test_hook,
// settings_write today; plugin-contributed kinds tomorrow) gets
// auto-added without the operator having to know the id.
const destructiveKindIds = computed<string[]>(() =>
  mcpKindOptions.value.filter((k) => k.danger === 'destructive').map((k) => k.id))

// Phase 7.5+++ — disable Lock-all-destructive preset when every Destructive
// kind is already in the picker. Prevents click-confusion (no-op feedback).
const allDestructiveAlreadyLocked = computed<boolean>(() => {
  if (destructiveKindIds.value.length === 0) return false
  const current = new Set(mcpAlwaysConfirmKindsArr.value)
  return destructiveKindIds.value.every((id) => current.has(id))
})

function lockAllDestructive(): void {
  // Union with current selection so the operator's existing custom
  // additions (e.g. plugin-specific kinds) aren't dropped.
  const merged = new Set([...mcpAlwaysConfirmKindsArr.value, ...destructiveKindIds.value])
  mcpAlwaysConfirmKindsArr.value = Array.from(merged)
}

// Phase 7.5+++ — operator-locale label for the kind. Shared lookup
// pattern with McpKinds + McpConfirmBanner: localized i18n key first,
// daemon-supplied label second, bare id last. Plugin-supplied kinds
// without translation still render their daemon label.
function humanKindLabel(opt: { id?: string; label?: string }): string {
  if (!opt.id) return opt.label || ''
  const key = `mcpKinds.labels.${opt.id}`
  return te(key) ? t(key) : (opt.label || opt.id)
}

function clearAlwaysConfirm(): void {
  mcpAlwaysConfirmKindsArr.value = []
}
// Phase 7.5+++ — janitor retention windows. Defaults match
// GrantSweeperService.Default* constants (1 day, 30 days). Setting
// either to 0 disables that branch (operator keeps everything).
const mcpExpiredRetentionDays = ref(1)
const mcpRevokedRetentionDays = ref(30)
const mcpToolCallRetentionDays = ref(30)
// Phase 7.1a — Deploy subsystem toggle. Default TRUE; mirrors daemon's
// own `deploy.enabled` setting. When false, SiteEdit Deploy tab hides and
// /api/nks.wdc.deploy/* endpoints 404. History rows stay in DB (additive).
const deployEnabled = ref(true)
// Phase 7.4 #109-D1 — operator-controlled flag for the upcoming plugin
// cutover. Default TRUE = current behaviour (host-native handlers in
// Program.cs serve every deploy route, plugin endpoints get skipped by
// the route-conflict guard). Switch is disabled in the UI today since
// plugin parity isn't yet proven; the value still round-trips so once
// phase B/C/D land the operator can flip without a redeploy.
const deployUseLegacyHostHandlers = ref(true)
// Phase 7.4 #109-D1+: toggle is enabled iff /api/admin/plugin-readiness
// reports readyToFlip:true. Today always false (phase B/C/D blockers
// surfaced through the readiness diagnostic). Once those phases ship
// and the endpoint flips readyToFlip → the switch unlocks automatically
// without a code change. Operator sees the lock state matches the live
// daemon's view of plugin parity, not a hardcoded hint.
//
// #109-D1+ iter 10: store full readiness so the locked toggle can
// surface blockers count inline — operator sees WHY at a glance without
// having to open DeploySettings panel popover.
// Iter 13: also store mode + pluginVersion + pluginLoaded so the section
// header can render a status banner ("⚙ built-in" / "🔌 plugin v0.1.0").
const deployFlipUnlocked = ref<boolean>(false)
const deployFlipBlockers = ref<string[]>([])
interface DeployBlockerDetail { summary: string; phase: string; remediation: string }
const deployFlipBlockerDetails = ref<DeployBlockerDetail[]>([])
const deployBackendMode = ref<'built-in' | 'plugin' | null>(null)
const deployPluginVersion = ref<string | null>(null)
const deployPluginLoaded = ref<boolean>(false)
// Iter 56-58 (#258) — restartPending becomes true when the operator has
// flipped useLegacyHostHandlers but the daemon still serves under the
// boot-time value. The global toggle is the place where the operator
// most commonly does this flip, so the banner here is even more
// important than the per-site popover.
const deployRestartPending = ref<boolean>(false)
// Iter 64 — gatedEndpoints[] mirrored from per-site popover so both
// surfaces show identical cutover scope. Empty when older daemon doesn't
// expose the field (graceful degradation).
const deployFlipGatedEndpoints = ref<string[]>([])
async function loadDeployFlipReadiness(): Promise<void> {
  try {
    // Iter 19: fetch with ?explain=true so the locked-toggle popover can
    // render per-blocker remediation. Older daemons return flat shape and
    // blockerDetails stays empty → popover falls back to summary list.
    const r = await fetch(`${daemonBaseUrl()}/api/admin/plugin-readiness?explain=true`, {
      method: 'GET',
      headers: authHeaders(),
    })
    if (r.ok) {
      const j = await r.json()
      deployFlipUnlocked.value = j.readyToFlip === true
      deployFlipBlockers.value = Array.isArray(j.blockers) ? j.blockers : []
      deployFlipBlockerDetails.value = Array.isArray(j.blockerDetails) ? j.blockerDetails : []
      deployBackendMode.value = j.mode === 'plugin' || j.mode === 'built-in' ? j.mode : null
      deployPluginVersion.value = typeof j.pluginVersion === 'string' ? j.pluginVersion : null
      deployPluginLoaded.value = j.pluginLoaded === true
      deployRestartPending.value = j.restartPending === true
      deployFlipGatedEndpoints.value = Array.isArray(j.gatedEndpoints) ? j.gatedEndpoints : []
    }
  } catch { /* keep locked on error */ }
}

// #147 — OS notification per-channel toggles. Persist via the
// osNotifications service (localStorage-backed) on flip so the change
// takes effect immediately without a Save All step.
const osNotifyDeploy = ref(isChannelEnabled('deploy'))
const osNotifyMcp = ref(isChannelEnabled('mcp'))
const osNotifySystem = ref(isChannelEnabled('system'))
watch(osNotifyDeploy, (v) => setChannelEnabled('deploy', v))
watch(osNotifyMcp,    (v) => setChannelEnabled('mcp', v))
watch(osNotifySystem, (v) => setChannelEnabled('system', v))

async function onTestOsNotify(): Promise<void> {
  await osNotify({
    title: t('settings.osNotify.testTitle'),
    body: t('settings.osNotify.testBody'),
    urgency: 'normal',
    channel: 'system',
  })
}
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
import { fetchBackups, createBackup, downloadBackup, listMcpKinds, type BackupEntry } from '../../api/daemon'
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

// Simple-mode aliases — same backing state as the advanced surface so a
// backup triggered from Simple Settings shows up in Advanced too.
const creatingBackup = backupCreating
const createBackupFromSimple = manualBackup
const httpPort = computed(() => ports.http)
const httpsPort = computed(() => ports.https)
const mysqlPort = computed(() => ports.mysql)

function downloadBackupFile(path: string) {
  downloadBackup(path)
}

// ── Danger-zone reset operations ──────────────────────────────────────
const resettingSettings = ref(false)
const resettingFactory = ref(false)

// Shared reset runner: POST the scope, surface HTTP/JSON errors clearly,
// then force a full window reload once the daemon-respawn has had time to
// pick a fresh port. Without the reload the user stays on a stale view —
// sites list still shows cached rows, toast flashes briefly, "vůbec nic
// neudělalo" impression. 3.5 s is the upper bound of our observed
// exit-99 → port-file → `/healthz` bounce window.
async function doReset(scope: 'settings' | 'factory', label: string): Promise<boolean> {
  const url = `${daemonBaseUrl()}/api/admin/reset?scope=${scope}`
  const r = await fetch(url, { method: 'POST', headers: authHeaders() })
  if (!r.ok) {
    const body = await r.text().catch(() => '')
    throw new Error(`HTTP ${r.status} — ${body || 'daemon returned no body'}`)
  }
  ElMessage.success({
    message: `${label} hotov. Daemon se restartuje a aplikace se znovu načte…`,
    duration: 3500,
  })
  // Give the daemon time to exit-99 → Electron respawn → port file → /healthz,
  // then ask the main process to force-reload the renderer. Main-side
  // reloadIgnoringCache bypasses cases where `window.location.reload()` in
  // the `app://` scheme kept Pinia state alive and the UI stayed stale
  // (user reported "sees same sites after reset" even with Ctrl+R).
  await new Promise(resolve => setTimeout(resolve, 3500))
  if (window.electronAPI?.restartRenderer) {
    await window.electronAPI.restartRenderer()
  } else {
    window.location.reload()
  }
  return true
}

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
    await doReset('settings', 'Reset nastavení')
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
    await doReset('factory', 'Tovární reset')
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

async function loadMcpKindOptions(): Promise<void> {
  try {
    const r = await listMcpKinds()
    mcpKindOptions.value = r.entries.map((e) => ({
      id: e.id,
      label: e.label,
      danger: e.danger,
    }))
  } catch {
    mcpKindOptions.value = []
  }
}

async function loadSettings() {
  try {
    const data = await fetchSettings()
    if (data['ports.http'])        ports.http = parseInt(data['ports.http'])
    if (data['ports.https'])       ports.https = parseInt(data['ports.https'])
    // Phase 6.23 — mcp.enabled flag. Stored as string in SQLite settings;
    // accept "true"/"1" as truthy, default false when missing.
    mcpEnabled.value = data['mcp.enabled'] === 'true' || data['mcp.enabled'] === '1'
    // Phase 7.4e — strict_kinds (default false: lenient).
    mcpStrictKinds.value = data['mcp.strict_kinds'] === 'true' || data['mcp.strict_kinds'] === '1'
    // Phase 7.5+++ — always-confirm kinds (comma-separated).
    mcpAlwaysConfirmKinds.value = data['mcp.always_confirm_kinds'] ?? ''
    // Phase 7.5+++ — janitor retention windows. parseInt yields NaN on
    // missing/empty; coalesce to defaults (1d/30d) so the inputs land
    // on sensible numbers when settings haven't been touched.
    {
      const exp = parseInt(data['mcp.grant_expired_retention_days'] ?? '')
      mcpExpiredRetentionDays.value = Number.isFinite(exp) && exp >= 0 ? exp : 1
      const rev = parseInt(data['mcp.grant_revoked_retention_days'] ?? '')
      mcpRevokedRetentionDays.value = Number.isFinite(rev) && rev >= 0 ? rev : 30
      // Phase 8 — mcp_tool_calls audit retention.
      const tc = parseInt(data['mcp.toolCallRetentionDays'] ?? '')
      mcpToolCallRetentionDays.value = Number.isFinite(tc) && tc >= 1 ? tc : 30
    }
    // Phase 7.1a — deploy.enabled flag. Default TRUE; only false when explicitly
    // set to "false"/"0". Mirrors daemon's IsDeployEnabled() helper.
    deployEnabled.value = !(data['deploy.enabled'] === 'false' || data['deploy.enabled'] === '0')
    // Phase 7.4 #109-D1 — same boolean parsing convention as deploy.enabled.
    // Default TRUE means current host-native behaviour stays in effect.
    deployUseLegacyHostHandlers.value = !(data['deploy.useLegacyHostHandlers'] === 'false'
      || data['deploy.useLegacyHostHandlers'] === '0')
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

async function importSettings(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
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
  input.value = ''
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
    updateCheck.hasUpdate = compareSemver(latest, currentVersion.value) > 0
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

// Iter 29: live-refresh readiness on deploy.* settings save from another
// tab. Mirrors the iter 28 wiring in DeploySettingsPanel — when operator
// flips useLegacyHostHandlers via API or another open Settings page, the
// locked-toggle popover reflects the new state without reload.
let unsubscribeDeploySettingsSse: (() => void) | null = null

onMounted(async () => {
  void loadSettings()
  void loadDatabases()
  void loadPhpVersions()
  void loadBackups()
  void loadDeviceId()
  void loadPluginCatalogStatus()
  void loadPluginPorts()
  void loadDeployFlipReadiness()
  unsubscribeDeploySettingsSse = subscribeEventsMap({
    'deploy:settings-changed': () => { void loadDeployFlipReadiness() },
  })
  void loadMysqlRootStatus()
  void loadSnapshots()
  // Phase 7.5+++ — fetch destructive op kinds for the always-confirm
  // multi-select. Best-effort: silent failure leaves the input as the
  // free-text fallback, no toast (kinds endpoint may be MCP-disabled).
  void loadMcpKindOptions()
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

onBeforeUnmount(() => {
  if (unsubscribeDeploySettingsSse) {
    unsubscribeDeploySettingsSse()
    unsubscribeDeploySettingsSse = null
  }
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
      'mcp.enabled':         String(mcpEnabled.value),
      'mcp.strict_kinds':    String(mcpStrictKinds.value),
      'mcp.always_confirm_kinds': mcpAlwaysConfirmKinds.value,
      'mcp.grant_expired_retention_days': String(mcpExpiredRetentionDays.value),
      'mcp.grant_revoked_retention_days': String(mcpRevokedRetentionDays.value),
      'mcp.toolCallRetentionDays': String(mcpToolCallRetentionDays.value),
      'deploy.enabled':      String(deployEnabled.value),
      'deploy.useLegacyHostHandlers': String(deployUseLegacyHostHandlers.value),
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
  background: transparent;
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px;
  margin-bottom: 0;
  border-bottom: 1px solid var(--wdc-accent-glow);
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}

.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  letter-spacing: -0.02em;
  color: var(--wdc-text);
  margin: 0;
}

.page-subtitle {
  font-size: 0.85rem;
  color: var(--wdc-text-3);
  margin-top: 4px;
}

.page-body {
  padding: 24px;
}

.simple-settings-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.35fr) minmax(320px, 0.65fr);
  gap: 16px;
  align-items: start;
}

.simple-settings-panel {
  min-width: 0;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 18px;
}

.simple-settings-panel-main {
  grid-row: span 2;
}

.simple-settings-panel-header {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 14px;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--wdc-border);
}

.simple-settings-panel-header h2 {
  margin: 0;
  color: var(--wdc-text);
  font-size: 1rem;
  font-weight: 800;
}

.simple-settings-panel-header p {
  margin: 3px 0 0;
  color: var(--wdc-text-2);
  font-size: 0.82rem;
  line-height: 1.4;
}

.simple-runtime-grid {
  display: grid;
  gap: 4px;
}

.simple-settings-actions {
  display: flex;
  gap: 8px;
  margin-top: 10px;
  flex-wrap: wrap;
}

.simple-settings-footer {
  grid-column: 1 / -1;
  margin-top: 0 !important;
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
  border: 1px solid var(--wdc-border);
  padding: 1px 7px;
  border-radius: var(--wdc-radius-sm);
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

@media (max-width: 980px) {
  .simple-settings-grid {
    grid-template-columns: minmax(0, 1fr);
  }

  .simple-settings-panel-main {
    grid-row: auto;
  }
}

@media (max-width: 640px) {
  .page-header {
    padding: 18px 16px 0;
    margin-bottom: 14px;
  }

  .page-body {
    padding: 0 16px 18px;
  }

  .simple-settings-panel {
    padding: 14px;
  }

  .settings-footer {
    flex-wrap: wrap;
  }

  .settings-footer :deep(.el-button) {
    flex: 1 1 140px;
    min-height: 36px;
  }
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

/* Update tab */
.mono { font-family: 'JetBrains Mono', monospace; font-size: 0.88rem; }
.text-muted { color: var(--wdc-text-3); font-size: 0.82rem; }
.update-actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; margin-top: 16px; }
</style>
