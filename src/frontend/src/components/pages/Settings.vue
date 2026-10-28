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
        <el-alert
          v-if="hiddenTabRequested"
          id="simple-hidden-tab"
          type="info"
          :closable="false"
          show-icon
          class="simple-hidden-tab"
          :title="$t('settings.simple.hiddenTab')"
        >
          <el-button size="small" type="primary" @click="openAdvancedTab(hiddenTabRequested)">
            {{ $t('settings.simple.switchToAdvanced') }}
          </el-button>
        </el-alert>
        <SettingsPanel
          id="simple-general"
          panel-class="simple-settings-panel-main"
          :title="$t('settings.tabs.general')"
          :subtitle="$t('settings.general.tabDesc')"
          :icon="Setting"
        >
          <EasyGeneralSettings
            :t="t"
            :locale="locale"
            :theme-mode="themeStore.mode"
            :is-advanced="uiModeStore.isAdvanced"
            :run-on-startup="runOnStartup"
            :run-on-startup-supported="runOnStartupSupported"
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
            @update:run-on-startup="setRunOnStartup"
            @update:default-php="saveDefaultPhp"
            @update:telemetry-enabled="setTelemetryConsent({ enabled: $event })"
            @update:telemetry-crash-reports="setTelemetryConsent({ crashReports: $event })"
            @flush-dns="flushDns"
            @discover-mamp="discoverMamp"
          />
        </SettingsPanel>

        <SettingsPanel
          id="simple-update"
          :title="$t('settings.tabs.update')"
          :subtitle="updateSubtitle"
          :icon="Download"
        >
          <EasyUpdateSettings
            :t="t"
            :current-version="currentVersion"
            :update-check="updateCheck"
            :latest-version="latestVersion"
            :last-checked-iso="lastCheckedIso"
            :has-update="hasUpdate"
            :updater="updaterState"
            :format-relative-time="formatRelativeTime"
            :render-release-notes="renderReleaseNotes"
            @check="runUpdateCheck"
            @install="installUpdate"
            @open-release="openReleasePage"
          />
        </SettingsPanel>

        <!-- Native OS notifications — same component as the Advanced
             "Notifications" section; changes apply immediately. -->
        <SettingsPanel
          id="simple-notifications"
          :title="$t('osNotifications.settings.title')"
          :icon="Bell"
        >
          <NotificationSettings :t="t" />
        </SettingsPanel>

        <SettingsPanel
          id="simple-mcp"
          :title="$t('settings.tabs.mcp')"
          :subtitle="$t('settings.mcpTab.simpleSubtitle')"
          :icon="Cpu"
        >
          <McpCapabilitiesSettings />
        </SettingsPanel>

        <!-- Simple Network panel — read-only port snapshot + DNS flush
             shortcut. The full per-plugin port editor stays in Advanced;
             beginners just need to see "what listens where" without
             leaving Simple. -->
        <SettingsPanel
          id="simple-network"
          :title="$t('settings.simple.network.title')"
          :subtitle="$t('settings.simple.network.subtitle')"
          :icon="Connection"
        >
          <div class="simple-runtime-grid">
            <div class="about-sys-row">
              <span class="sys-label">{{ $t('settings.simple.apacheHttp') }}</span>
              <span class="sys-value mono">:{{ httpPort }}</span>
            </div>
            <div class="about-sys-row">
              <span class="sys-label">{{ $t('settings.simple.apacheHttps') }}</span>
              <span class="sys-value mono">:{{ httpsPort }}</span>
            </div>
            <div class="about-sys-row">
              <span class="sys-label">MySQL</span>
              <span class="sys-value mono">:{{ mysqlPort }}</span>
            </div>
          </div>
          <template #actions>
            <el-button size="small" @click="openAdvancedTab('ports')">
              {{ $t('settings.simple.network.editInAdvanced') }} →
            </el-button>
          </template>
        </SettingsPanel>

        <!-- Plan §4 (Easy Settings IA): Services — addresses user
             feedback "kde stav sluzeb". Lists running/stopped state per
             service with link to the per-service plugin page for full
             control. Full start/stop UX stays on Dashboard. -->
        <SettingsPanel
          v-if="daemonStore.visibleServices.length > 0"
          :title="$t('settings.simple.services.title')"
          :subtitle="$t('settings.simple.services.subtitle')"
          :icon="Operation"
        >
          <div class="simple-runtime-grid">
            <div
              v-for="svc in daemonStore.visibleServices"
              :key="svc.id"
              class="about-sys-row"
            >
              <span class="sys-label">
                <HealthStatusDot :level="serviceDot(svc)" />
                {{ svc.displayName || svc.name || svc.id }}
              </span>
              <span class="sys-value">{{ serviceStateLabel(svc) }}</span>
            </div>
          </div>
          <template #actions>
            <el-button size="small" @click="$router.push('/dashboard')">
              {{ $t('settings.simple.services.manageOnDashboard') }} →
            </el-button>
          </template>
        </SettingsPanel>

        <!-- Simple Backup panel — one-click "create a backup now". The
             retention + scheduler controls live in Advanced; Simple just
             surfaces the safety net. -->
        <SettingsPanel
          :title="$t('settings.simple.backup.title')"
          :subtitle="$t('settings.simple.backup.subtitle')"
          :icon="FolderOpened"
        >
          <template #actions>
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
          </template>
        </SettingsPanel>

        <!-- Plan §4 (Easy Settings IA): Account & Sync — was previously
             advanced-only, blocking simple-mode users from signing in.
             Wraps the same SSO/password/sync cards Advanced uses, just
             scoped to the simple-settings-grid. -->
        <SettingsPanel
          id="simple-account"
          :title="$t('settings.tabs.account')"
          :subtitle="$t('settings.account.simpleSubtitle')"
          :icon="User"
        >
          <AccountSsoCard
            :t="$t"
            :is-authenticated="authStore.isAuthenticated"
            :display-name="authStore.displayName"
            :login-pending="authStore.loginPending"
            :login-error="authStore.loginError"
            @login="ssoLogin"
            @logout="authStore.logout()"
          />
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
          <AccountSimpleSyncCard
            v-else
            :t="$t"
            :title="$t('settings.sync.cloudSync')"
            :email="accountEmail"
            :syncing="syncing"
            :pulling="pulling"
            @push="pushToCloud"
            @pull="pullFromCloud"
            @logout="doLogout"
          />
        </SettingsPanel>

        <SettingsPanel
          v-if="systemInfo"
          id="simple-about"
          panel-class="simple-runtime-panel"
          :title="$t('settings.tabs.about')"
          :subtitle="`NKS WebDev Console v${appVersion}`"
          :icon="InfoFilled"
        >
          <div class="simple-runtime-grid">
            <div class="about-sys-row">
              <span class="sys-label">{{ $t('settings.about.services') }}</span>
              <span class="sys-value">{{ daemonStore.runningServices.length }}/{{ installedServiceCount }}</span>
            </div>
            <div class="about-sys-row">
              <span class="sys-label">{{ $t('settings.about.sites') }}</span>
              <span class="sys-value">{{ systemInfo.sites }}</span>
            </div>
            <div v-if="systemInfo.daemon?.version" class="about-sys-row">
              <span class="sys-label">{{ $t('settings.about.daemonVersion') }}</span>
              <span class="sys-value mono" :title="systemInfo.daemon.version">{{ shortVersion(systemInfo.daemon.version) }}</span>
            </div>
            <div v-if="systemInfo.daemon?.uptime !== undefined" class="about-sys-row">
              <span class="sys-label">{{ $t('settings.about.daemonUptime') }}</span>
              <span class="sys-value">{{ formatUptime(systemInfo.daemon.uptime) }}</span>
            </div>
          </div>
        </SettingsPanel>

      </div>

      <SettingsShell
        v-else
        v-model="activeTab"
        :sections="settingsSections"
        :group-labels="settingsGroupLabels"
        :aria-label="$t('settings.title')"
      >
        <!-- Ports tab -->
        <section v-show="activeTab === 'ports'" class="shell-section">
          <div class="tab-content">
            <p class="tab-desc">{{ $t('settings.ports.description') }}</p>

            <!-- Task 15: plugin-owned ports. Pulled from GET /api/plugins/ports
                 (IPortMetadata DI registrations per task 25). Only active
                 plugins show up — inactive ones are hidden so the user
                 doesn't see rows for services that aren't running. -->
            <SettingsCard
              v-if="pluginPorts.length > 0"
              :title="$t('settings.advancedPorts.pluginPorts')"
              style="margin-bottom: var(--wdc-space-4)"
            >
              <template #meta>
                <span style="font-size: 0.72rem; color: var(--wdc-text-3)">{{ $t('settings.advancedPorts.active', { n: pluginPorts.length }) }}</span>
              </template>
              <el-form label-position="left" label-width="200px" size="small">
                <el-form-item
                  v-for="p in pluginPorts"
                  :key="p.pluginId + ':' + p.key"
                  :label="p.label"
                >
                  <el-input-number
                    :model-value="p.currentPort"
                    :min="1"
                    :max="65535"
                    class="port-input"
                    disabled
                  />
                  <div class="hint">
                    <code class="mono">{{ p.pluginId }}</code> · {{ $t('settings.advancedPorts.defaultPort', { port: p.defaultPort }) }}
                  </div>
                </el-form-item>
              </el-form>
            </SettingsCard>

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
            <SettingsCard :title="$t('settings.tabs.ports')">
              <el-alert
                type="info"
                :closable="false"
                show-icon
                style="margin-bottom: var(--wdc-space-3)"
              >
                <template #title>{{ $t('settings.advancedPorts.portAlertTitle') }}</template>
                {{ $t('settings.advancedPorts.portAlertDesc') }}
              </el-alert>
              <el-alert
                v-if="duplicatePorts.length > 0"
                type="error"
                :closable="false"
                show-icon
                style="margin-bottom: var(--wdc-space-3)"
                :title="$t('settings.ports.duplicate', { ports: duplicatePorts.join(', ') })"
              />
              <el-form label-position="left" label-width="200px" size="small">
                <el-form-item :label="$t('settings.ports.httpPort')">
                  <el-input-number v-model="ports.http" :min="1" :max="65535" class="port-input" />
                </el-form-item>
                <el-form-item :label="$t('settings.ports.httpsPort')">
                  <el-input-number v-model="ports.https" :min="1" :max="65535" class="port-input" />
                </el-form-item>
                <el-form-item :label="$t('settings.ports.mysqlPort')">
                  <el-input-number v-model="ports.mysql" :min="1" :max="65535" class="port-input" />
                </el-form-item>
                <el-form-item :label="$t('settings.ports.redisPort')">
                  <el-input-number v-model="ports.redis" :min="1" :max="65535" class="port-input" />
                </el-form-item>
                <el-form-item :label="$t('settings.ports.mailpitSmtp')">
                  <el-input-number v-model="ports.mailpitSmtp" :min="1" :max="65535" class="port-input" />
                </el-form-item>
                <el-form-item :label="$t('settings.ports.mailpitHttp')">
                  <el-input-number v-model="ports.mailpitHttp" :min="1" :max="65535" class="port-input" />
                </el-form-item>
                <el-form-item :label="$t('settings.ports.postgresqlPort')">
                  <el-input-number v-model="ports.postgresql" :min="1" :max="65535" class="port-input" />
                </el-form-item>
                <el-form-item :label="$t('settings.ports.phpFpmBase')">
                  <el-input-number v-model="phpFpmBasePort" :min="9000" :max="9999" class="port-input" />
                  <div class="hint">{{ $t('settings.ports.phpFpmFormula') }}</div>
                </el-form-item>
              </el-form>
            </SettingsCard>
          </div>
        </section>

        <!-- General tab -->
        <section v-show="activeTab === 'general'" class="shell-section">
          <EasyGeneralSettings
            :t="t"
            :locale="locale"
            :theme-mode="themeStore.mode"
            :is-advanced="uiModeStore.isAdvanced"
            :run-on-startup="runOnStartup"
            :run-on-startup-supported="runOnStartupSupported"
            :default-php="defaultPhp"
            :php-versions="phpVersions"
            :flushing-dns="flushingDns"
            :mamp-discovering="mampDiscovering"
            :telemetry-enabled="telemetryEnabled"
            :telemetry-crash-reports="telemetryCrashReports"
            @update:locale="onLocaleChange"
            @update:theme-mode="themeStore.setMode"
            @update:ui-mode="uiModeStore.setUiMode"
            @update:run-on-startup="setRunOnStartup"
            @update:default-php="saveDefaultPhp"
            @update:telemetry-enabled="setTelemetryConsent({ enabled: $event })"
            @update:telemetry-crash-reports="setTelemetryConsent({ crashReports: $event })"
            @flush-dns="flushDns"
            @discover-mamp="discoverMamp"
          />
        </section>
        <!-- Paths tab -->
        <section v-show="activeTab === 'paths'" class="shell-section">
          <div class="tab-content">
            <SettingsCard :title="$t('settings.paths.dataDir')">
              <el-input :model-value="systemInfo?.dataDir ?? ''" readonly class="mono-input">
                <template v-if="systemInfo?.dataDir" #append>
                  <el-button @click="revealDataDir">{{ $t('settings.paths.showInFolder') }}</el-button>
                </template>
              </el-input>
              <div class="hint">{{ $t('settings.paths.dataHint') }}</div>
            </SettingsCard>
            <SettingsCard :title="$t('settings.advancedBackups.title')">
              <p class="tab-desc">{{ $t('settings.paths.backupsDesc') }}</p>
              <div class="paths-backup-actions">
                <el-button size="small" type="primary" :loading="backupCreating" @click="manualBackup">
                  {{ $t('settings.advancedBackups.create') }}
                </el-button>
                <el-button size="small" @click="$router.push('/backups')">
                  {{ $t('settings.paths.manageBackups') }} →
                </el-button>
              </div>
            </SettingsCard>
          </div>
        </section>

        <!-- Advanced tab — integration endpoints -->
        <!-- What AI clients connected over MCP may do. Toggles save
             immediately; the daemon refuses them from the MCP token. -->
        <section v-show="activeTab === 'mcp'" class="shell-section">
          <div class="tab-content mcp-tab">
            <!-- First row of the capability list is the MCP master switch. -->
            <McpCapabilitiesSettings v-if="activeTab === 'mcp'" />
            <!-- Options that only matter with MCP on; they save on change,
                 like the switches above. -->
            <div v-if="mcpEnabled" class="settings-section" id="mcp-section">
              <h4 class="section-title">{{ $t('settings.mcpTab.optionsTitle') }}</h4>
              <el-form label-position="top" size="small">
                <!-- Phase 7.4e — strict kind validation. Default OFF (lenient
                     mode where any regex-valid kind passes). Turn ON to refuse
                     any kind not registered via IDestructiveOperationKinds. -->
                <el-form-item :label="$t('settings.mcp.strictKindsLabel')">
                  <el-switch :model-value="mcpStrictKinds" @update:model-value="setMcpStrictKinds" />
                  <div class="hint" style="margin-top: var(--wdc-space-1)">{{ $t('settings.mcp.strictKindsHint') }}</div>
                </el-form-item>
                <!-- Phase 7.5+++ — always-confirm kinds override. Comma-
                     separated list (e.g. "restore,cancel") of kind ids
                     for which the validator skips grant auto-approval.
                     Operator's "ring-fence the riskiest ops" knob: even
                     wildcard always-grants must yield to the GUI banner
                     for these kinds. -->
                <el-form-item :label="$t('settings.mcp.alwaysConfirmKindsLabel')">
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
                    @change="saveAlwaysConfirmKinds"
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
                      <span class="muted" style="margin-left: var(--wdc-space-2); font-size: 11px">
                        {{ opt.id }} · {{ $t('mcpKinds.danger.' + opt.danger) }}
                      </span>
                    </el-option>
                  </el-select>
                  <el-input
                    v-else
                    v-model="mcpAlwaysConfirmKinds"
                    placeholder="restore,cancel"
                    @change="saveAlwaysConfirmKinds"
                    style="max-width: 320px"
                  />
                  <!-- Phase 7.5+++ — one-click presets. "Lock all destructive"
                       fills the picker with every kind tagged Destructive
                       (restore, test_hook, settings_write, plus any plugin-
                       contributed dangerous kinds). "Clear" resets. -->
                  <div v-if="mcpKindOptions.length > 0" style="margin-top: var(--wdc-space-1-5)">
                    <el-button
                      size="small"
                      :disabled="destructiveKindIds.length === 0 || allDestructiveAlreadyLocked"
                      @click="lockAllDestructive"
                    >
                      <el-icon><Lock /></el-icon>
                      <span>{{ $t('settings.mcp.lockAllDestructive', { n: destructiveKindIds.length }) }}</span>
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
                  <div class="hint" style="margin-top: var(--wdc-space-1)">{{ $t('settings.mcp.alwaysConfirmKindsHint') }}</div>
                </el-form-item>
                <!-- Phase 7.5+++ — operator-tunable janitor retention.
                     Defaults match GrantSweeperService.Default* (1d/30d).
                     Setting either to 0 disables that branch (keep all). -->
                <el-form-item :label="$t('settings.mcp.expiredRetentionLabel')">
                  <el-input-number v-model="mcpExpiredRetentionDays"
                    :min="0" :max="365" controls-position="right" style="width: 120px"
                    @change="(v: number | undefined) => saveMcpNumber('mcp.grant_expired_retention_days', v)" />
                  <span class="hint" style="margin-left: var(--wdc-space-2)">{{ $t('settings.mcp.daysSuffix') }}</span>
                  <div class="hint" style="margin-top: var(--wdc-space-1)">{{ $t('settings.mcp.expiredRetentionHint') }}</div>
                </el-form-item>
                <el-form-item :label="$t('settings.mcp.revokedRetentionLabel')">
                  <el-input-number v-model="mcpRevokedRetentionDays"
                    :min="0" :max="365" controls-position="right" style="width: 120px"
                    @change="(v: number | undefined) => saveMcpNumber('mcp.grant_revoked_retention_days', v)" />
                  <span class="hint" style="margin-left: var(--wdc-space-2)">{{ $t('settings.mcp.daysSuffix') }}</span>
                  <div class="hint" style="margin-top: var(--wdc-space-1)">{{ $t('settings.mcp.revokedRetentionHint') }}</div>
                </el-form-item>
                <!-- Phase 8 — mcp_tool_calls audit retention. Drives the
                     hourly McpToolCallsSweeperService. Bumping this
                     keeps history for forensics; lowering reclaims disk
                     when traffic is heavy. -->
                <el-form-item :label="$t('settings.mcp.toolCallRetentionLabel')">
                  <el-input-number v-model="mcpToolCallRetentionDays"
                    :min="1" :max="365" controls-position="right" style="width: 120px"
                    @change="(v: number | undefined) => saveMcpNumber('mcp.toolCallRetentionDays', v)" />
                  <span class="hint" style="margin-left: var(--wdc-space-2)">{{ $t('settings.mcp.daysSuffix') }}</span>
                  <div class="hint" style="margin-top: var(--wdc-space-1)">{{ $t('settings.mcp.toolCallRetentionHint') }}</div>
                </el-form-item>
              </el-form>
            </div>
          </div>
        </section>

        <section v-show="activeTab === 'advanced'" class="shell-section">
          <div class="tab-content">
            <p class="tab-desc">{{ $t('settings.advanced.tabDesc') }}</p>
            <el-form label-position="top" size="small">
              <el-form-item :label="$t('settings.advanced.catalogUrl')">
                <el-input
                  v-model="catalogUrl"
                  placeholder="https://wdc.nks-hub.cz"
                  class="mono-input"
                >
                  <template #append>
                    <el-button :loading="refreshingCatalog" @click="refreshCatalog">
                      {{ $t('settings.advanced.saveAndRefresh') }}
                    </el-button>
                  </template>
                </el-input>
                <div class="hint">{{ $t('settings.advanced.catalogUrlHint') }}</div>
                <div class="hint" v-if="catalogStatus">
                  <HealthStatusDot :level="catalogStatus.ok ? 'ok' : 'err'" />
                  {{ catalogStatus.message }}
                </div>
              </el-form-item>
              <el-form-item :label="$t('settings.advanced.binaryReleases')">
                <el-button size="small" @click="testCatalogReachable" :loading="testingCatalog">
                  {{ $t('settings.advanced.testConnection') }}
                </el-button>
                <el-button
                  v-if="catalogUrl"
                  size="small"
                  @click="openCatalogAdmin"
                >
                  {{ $t('settings.advanced.openCatalogAdmin') }}
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
                  {{ mysqlRootExists ? $t('settings.advanced.mysqlRootStored') : $t('settings.advanced.mysqlRootMissing') }}
                  {{ $t('settings.advanced.mysqlRootHint') }}
                </div>
              </el-form-item>
              <el-form-item :label="$t('settings.advanced.pluginAutoSync')">
                <el-switch v-model="pluginAutoSync" />
                <el-button
                  size="small"
                  style="margin-left: var(--wdc-space-3)"
                  :loading="syncingPlugins"
                  @click="syncPluginsNow"
                >{{ $t('settings.advanced.syncNow') }}</el-button>
                <div class="hint">{{ $t('settings.advanced.pluginAutoSyncHint') }}</div>
                <div v-if="pluginSyncStatus" class="hint">
                  <HealthStatusDot :level="pluginSyncStatus.ok ? 'ok' : 'err'" />
                  {{ pluginSyncStatus.message }}
                </div>
                <div v-if="pluginCatalogStatus" class="hint">
                  {{ $t('settings.advanced.pluginCatalogLine', {
                    count: pluginCatalogStatus.catalogCount,
                    cached: pluginCatalogStatus.cachedCount,
                    ago: pluginCatalogStatus.lastFetch ? formatRelativeTime(pluginCatalogStatus.lastFetch) : $t('settings.advanced.neverSynced'),
                  }) }}
                </div>
              </el-form-item>
            </el-form>

            <!-- Plan §5.advanced (item 486) — rare/destructive controls
                 behind explicit progressive disclosure. The danger zone
                 was previously always-expanded which encouraged accidental
                 scroll-past; now requires explicit toggle to reveal both
                 reset and factory-reset actions. -->
            <el-collapse v-model="dangerZoneExpanded" class="danger-collapse">
              <el-collapse-item name="danger">
                <template #title>
                  <span class="danger-collapse-title">
                    <el-icon><Warning /></el-icon>
                    {{ $t('settings.danger.title') }}
                  </span>
                </template>
                <div class="danger-zone">
                  <p class="danger-desc">
                    {{ $t('settings.danger.desc') }}
                  </p>
                  <div class="danger-row">
                <div class="danger-info">
                  <strong>{{ $t('settings.danger.resetSettings') }}</strong>
                  <span class="hint">
                    {{ $t('settings.danger.resetSettingsHint') }}
                  </span>
                </div>
                <el-button
                  type="warning"
                  :loading="resettingSettings"
                  @click="confirmResetSettings"
                >{{ $t('settings.danger.resetSettings') }}</el-button>
              </div>
              <div class="danger-row">
                <div class="danger-info">
                  <strong>{{ $t('settings.danger.factoryResetHeading') }}</strong>
                  <span class="hint">
                    {{ $t('settings.danger.factoryResetHint') }}
                  </span>
                </div>
                <el-button
                  type="danger"
                  :loading="resettingFactory"
                  @click="confirmFactoryReset"
                >{{ $t('settings.danger.factoryReset') }}</el-button>
                </div>
                </div>
              </el-collapse-item>
            </el-collapse>

            <!-- Phase 7.1a — deploy subsystem toggle. Default ON since
                 most users install WDC FOR site management with deploy.
                 OFF hides Deploy tab in SiteEdit + plugin REST endpoints
                 return 404. Useful for installs that only use WDC as
                 local Apache/MySQL manager without remote deploys. -->
            <div id="deploy-subsystem" class="settings-section" style="margin-top: var(--wdc-space-4)">
              <h4 class="section-title">{{ $t('settings.deploySubsystem.title') }}</h4>
              <p class="hint">
                {{ $t('settings.deploySubsystem.description') }}
              </p>
              <el-form label-position="left" label-width="240px" size="small">
                <el-form-item :label="$t('settings.deploySubsystem.enableLabel')">
                  <el-switch v-model="deployEnabled" />
                </el-form-item>
                <!-- The built-in/plugin backend switch stays out of sight until
                     there is something to do with it: readiness unlocked the
                     flip, a flip waits for a restart, or the plugin already
                     serves deploys. Locked, it only showed migration internals. -->
                <template v-if="deployFlipUnlocked || deployRestartPending || deployBackendMode === 'plugin'">
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
                  style="margin-bottom: var(--wdc-space-3); max-width: 480px"
                />
                <el-form-item :label="$t('settings.deploySubsystem.legacyHandlersLabel')">
                  <el-tooltip
                    :content="$t('settings.deploySubsystem.legacyHandlersTooltip')"
                    placement="right"
                  >
                    <el-switch v-model="deployUseLegacyHostHandlers" :disabled="!deployFlipUnlocked && !deployRestartPending" />
                  </el-tooltip>
                  <span class="hint" style="margin-left: var(--wdc-space-3)">
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
                        style="margin-left: var(--wdc-space-3); cursor: pointer"
                      >
                        <el-icon><Lock /></el-icon>
                        {{ $t('settings.deploySubsystem.legacyHandlersLocked', { n: deployFlipBlockers.length }) }}
                      </el-tag>
                    </template>
                    <!-- Iter 21: shared component renders blockerDetails
                         phase tag + remediation. Same shape as the per-site
                         DeploySettingsPanel popover so operator sees identical
                         UI in both global + per-site contexts.
                         Iter 24: localized recommendation header mirrors the
                         per-site popover for visual symmetry. -->
                    <div style="font-size: 12px">
                      <div style="margin-bottom: var(--wdc-space-1-5)">
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
                        style="margin-top: var(--wdc-space-2-5)"
                      >
                        <div class="muted" style="margin-bottom: var(--wdc-space-1); font-size: 11px">
                          {{ $t('deploySettings.gatedEndpointsLabel', { n: deployFlipGatedEndpoints.length }) }}
                        </div>
                        <ul style="font-size: 11px; margin: 0; padding-left: var(--wdc-space-4-5); max-height: 140px; overflow-y: auto">
                          <li v-for="ep in deployFlipGatedEndpoints" :key="ep">
                            <code class="mono">{{ ep }}</code>
                          </li>
                        </ul>
                      </div>
                    </div>
                  </el-popover>
                </el-form-item>
                <div class="hint">
                  {{ deployBackendMode === 'plugin'
                    ? $t('settings.deploySubsystem.modePlugin', { v: deployPluginVersion ?? '?' })
                    : $t('settings.deploySubsystem.modeBuiltIn') }}
                </div>
                </template>
              </el-form>
            </div>
          </div>
        </section>

        <!-- Account & Devices tab -->
        <section v-show="activeTab === 'account'" class="shell-section">
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
                :title="$t('settings.account.passwordTitle')"
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
                :title="$t('settings.sync.cloudSync')"
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
        </section>

        <!-- Update tab — visible in both Simple and Advanced -->
        <section v-show="activeTab === 'update'" class="shell-section">
          <div class="tab-content">
            <p class="tab-desc">{{ $t('settings.update.tabDesc') }}</p>
            <EasyUpdateSettings
              :t="t"
              :current-version="currentVersion"
              :update-check="updateCheck"
              :latest-version="latestVersion"
              :last-checked-iso="lastCheckedIso"
              :has-update="hasUpdate"
              :updater="updaterState"
              :format-relative-time="formatRelativeTime"
              :render-release-notes="renderReleaseNotes"
              @check="runUpdateCheck"
              @install="installUpdate"
              @open-release="openReleasePage"
            />
          </div>
        </section>
        <!-- Notifications tab — visible in both Simple and Advanced. Stored
             by the Electron main process, so no Save step is involved. -->
        <section v-show="activeTab === 'notifications'" class="shell-section">
          <SettingsCard :title="$t('osNotifications.settings.title')">
            <NotificationSettings :t="t" />
          </SettingsCard>
        </section>
        <!-- Sync tab — cloud config sync + export/import -->
        <section v-show="activeTab === 'sync'" class="shell-section">
          <div class="tab-content">
            <p class="tab-desc">{{ $t('settings.sync.topDesc') }}</p>

            <!-- Device identity -->
            <SyncDeviceIdentityCard
              :t="$t"
              :device-id="deviceId"
              v-model:device-name="deviceName"
              @copy="copyDeviceId"
            />

            <!-- Cloud sync needs the catalog account; without it every call fails. -->
            <el-alert
              v-if="!accountToken"
              type="info"
              :closable="false"
              show-icon
              :title="$t('settings.sync.signInFirst')"
              class="sync-sign-in"
            >
              <el-button size="small" type="primary" @click="activeTab = 'account'">{{ $t('settings.sync.goToAccount') }}</el-button>
            </el-alert>
            <SyncCloudCard
              :t="$t"
              :sync-status="syncStatus"
              :last-sync-time="lastSyncTime"
              :last-sync-display="lastSyncDisplay"
              :syncing="syncing"
              :pulling="pulling"
              :checking-cloud="checkingCloud"
              :disabled="!accountToken"
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
        </section>

        <!-- About tab -->
        <section v-show="activeTab === 'about'" class="shell-section">
          <div class="tab-content">
            <div class="about-card">
              <div class="about-logo">NKS WebDev Console</div>
              <div class="about-version">v{{ appVersion }}</div>
              <div class="about-subtitle">{{ $t('settings.about.subtitle') }}</div>
              <div class="about-desc">{{ $t('settings.about.description') }}</div>

              <!-- F85: Repo sources + docs — make the multi-repo architecture
                   discoverable from inside the app instead of only inside the
                   README on GitHub. -->
              <div class="about-links">
                <a href="https://github.com/nks-hub/webdev-console" target="_blank" class="about-link">{{ $t('settings.about.links.app') }}</a>
                <a href="https://github.com/nks-hub/webdev-console-plugins" target="_blank" class="about-link">{{ $t('settings.about.links.plugins') }}</a>
                <a href="https://github.com/nks-hub/wdc-catalog-api" target="_blank" class="about-link">{{ $t('settings.about.links.catalog') }}</a>
                <a href="https://github.com/nks-hub/webdev-console-binaries" target="_blank" class="about-link">{{ $t('settings.about.links.binaries') }}</a>
                <a href="https://wdc.nks-hub.cz" target="_blank" class="about-link">{{ $t('settings.about.website') }}</a>
              </div>


              <!-- F91.4: SSO login moved to Account tab (was here pre-F91.4).
                   Catalog status row stays — it's runtime info, not auth. -->
              <div v-if="pluginCatalogStatus" class="about-sso-status">
                <HealthStatusDot :level="pluginCatalogStatus.lastFetch ? 'ok' : 'muted'" />
                <span class="sys-label">{{ $t('settings.sso.catalog') }}</span>
                <span class="sys-value">
                  {{ pluginCatalogStatus.lastFetch
                      ? $t('settings.sso.catalogReachable', { ago: formatRelativeTime(pluginCatalogStatus.lastFetch) })
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
                  style="margin-left: var(--wdc-space-2)"
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
                  <span class="sys-value mono" :title="systemInfo.daemon.version">{{ shortVersion(systemInfo.daemon.version) }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.services') }}</span>
                  <span class="sys-value">{{ daemonStore.runningServices.length }}/{{ daemonStore.visibleServices.length }} {{ $t('dashboard.running') }}</span>
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
                <div class="about-sys-row">
                  <span class="sys-label">{{ $t('settings.about.os') }}</span>
                  <span class="sys-value">{{ systemInfo.os?.name ?? systemInfo.os?.version }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">.NET</span>
                  <span class="sys-value">{{ systemInfo.runtime?.dotnet }} ({{ systemInfo.runtime?.arch }})</span>
                </div>
                <div v-if="installedVersions.length" class="about-sys-title" style="margin-top: var(--wdc-space-3)">{{ $t('settings.about.installedVersions') }}</div>
                <div v-for="bin in installedVersions" :key="bin.app" class="about-sys-row">
                  <span class="sys-label">{{ appDisplayName(bin.app) }}</span>
                  <span class="sys-value">{{ bin.version }}</span>
                </div>
              </div>
            </div>
          </div>
        </section>
        <!-- Only while something is unsaved. Switches and buttons that act on
             their own (MCP, notifications, account, sync actions) never set it. -->
        <template v-if="isDirty && activeTab !== 'mcp'" #footer>
          <span class="footer-unsaved">{{ $t('settings.unsaved.label', changedCount) }}</span>
          <el-button type="primary" size="small" :loading="saving" @click="save">{{ $t('settings.unsaved.save') }}</el-button>
          <el-button size="small" @click="discardChanges">{{ $t('settings.unsaved.discard') }}</el-button>
        </template>
      </SettingsShell>

    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, onBeforeUnmount, onActivated, watch } from 'vue'
import { useRoute, useRouter, onBeforeRouteLeave } from 'vue-router'
import { useI18n } from 'vue-i18n'
import SettingsShell, { type SettingsSection } from '../settings/SettingsShell.vue'
import McpCapabilitiesSettings from '../settings/McpCapabilitiesSettings.vue'
import { setLocale, type Locale } from '../../i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Setting, Download, Connection, FolderOpened, InfoFilled, User, Operation, Warning, Bell, Lock, Cpu } from '@element-plus/icons-vue'
import { useThemeStore } from '../../stores/theme'
import { useUiModeStore } from '../../stores/uiMode'
import { useAuthStore } from '../../stores/auth'
import { useDaemonStore } from '../../stores/daemon'
import { useFeatureFlagsStore } from '../../stores/featureFlags'
import {
  catalogRegister, catalogLogin, fetchDevices, pushConfigToDevice,
  daemonBaseUrl, daemonAuthHeaders as authHeaders,
  fetchPhpVersions, fetchSettings, saveSettings,
  subscribeEventsMap,
  type DeviceInfo as CatalogDeviceInfo,
  type SystemInfo,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { formatBytes } from '../../utils/format'
import { backupContentLabel } from '../../utils/backupContent'
import HealthStatusDot from '../shared/HealthStatusDot.vue'
import SettingsPanel from '../shared/SettingsPanel.vue'
import SettingsCard from '../settings/shared/SettingsCard.vue'
import NotificationSettings from '../settings/shared/NotificationSettings.vue'
import ReadinessBlockerList from '../deploy/ReadinessBlockerList.vue'
import AccountAdvancedSummaryCard from '../settings/account/AccountAdvancedSummaryCard.vue'
import AccountDeviceTableCard from '../settings/account/AccountDeviceTableCard.vue'
import AccountPasswordCard from '../settings/account/AccountPasswordCard.vue'
import AccountSimpleSyncCard from '../settings/account/AccountSimpleSyncCard.vue'
import AccountSsoCard from '../settings/account/AccountSsoCard.vue'
import EasyGeneralSettings from '../settings/easy/EasyGeneralSettings.vue'
import EasyUpdateSettings, { type EasyUpdateCheck } from '../settings/easy/EasyUpdateSettings.vue'
import SyncCloudCard from '../settings/sync/SyncCloudCard.vue'
import SyncDeviceIdentityCard from '../settings/sync/SyncDeviceIdentityCard.vue'
import SyncExportImportCard from '../settings/sync/SyncExportImportCard.vue'
import SyncSnapshotsCard from '../settings/sync/SyncSnapshotsCard.vue'
import { compareSemver, versionCore } from '../../utils/semver'
import { pickSyncableSettings } from '../../utils/settingsSync'
import { appDisplayName } from '../../utils/binaries'
import { useAppVersion } from '../../utils/appVersion'

const versionRef = useAppVersion()
const appVersion = computed(() => versionRef.value.full)
const themeStore = useThemeStore()
const uiModeStore = useUiModeStore()
const authStore = useAuthStore()
const daemonStore = useDaemonStore()

async function ssoLogin() {
  const url = catalogUrl.value || 'https://wdc.nks-hub.cz'
  try {
    await authStore.login(url)
    // F91.9: immediately pull the authoritative profile so the UI can
    // switch from "Signed in" to "Signed in as x@y.cz" without waiting
    // for the user to navigate away and back.
    await authStore.refreshProfile(url)
    ElMessage.success(authStore.displayName
      ? t('settingsToast.signedInAs', { email: authStore.displayName })
      : t('settingsToast.signedIn'))
    // Register this device once, right after signing in (not on every visit).
    void autoRegisterDeviceAfterLogin()
  } catch (err) {
    ElMessage.error(t('settingsToast.ssoFailed', { err: errorMessage(err) }))
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
  // setLocale persists the choice and updates <html lang> so screen
  // readers and hyphenation follow the UI language.
  setLocale(v as Locale)
}

const activeTab = ref('general')

// Advanced-mode section list for SettingsShell, grouped by what the
// settings affect (the app itself, the local environment, integrations).
const settingsGroupLabels = computed(() => ({
  app: t('settings.groups.app'),
  environment: t('settings.groups.environment'),
  integration: t('settings.groups.integration'),
}))
const settingsSections = computed<SettingsSection[]>(() => [
  { name: 'general', label: t('settings.tabs.general'), group: 'app' },
  { name: 'account', label: t('settings.tabs.account'), group: 'app' },
  { name: 'update', label: t('settings.tabs.update'), group: 'app' },
  { name: 'notifications', label: t('settings.tabs.notifications'), group: 'app' },
  { name: 'about', label: t('settings.tabs.about'), group: 'app' },
  { name: 'ports', label: t('settings.tabs.ports'), group: 'environment' },
  { name: 'paths', label: t('settings.tabs.paths'), group: 'environment' },
  { name: 'sync', label: t('settings.tabs.sync'), group: 'integration' },
  { name: 'mcp', label: t('settings.tabs.mcp'), group: 'integration' },
  { name: 'advanced', label: t('settings.tabs.advanced'), group: 'integration' },
])

const ADVANCED_ONLY_TABS = new Set(['ports', 'paths', 'advanced', 'sync'])
watch(() => uiModeStore.isSimple, (simple) => {
  if (!simple) hiddenTabRequested.value = null
  if (simple && ADVANCED_ONLY_TABS.has(activeTab.value)) {
    activeTab.value = 'general'
  }
})

// Phase 7.5+++ — deep-link target. Supports ?tab=advanced&scroll=mcp-section
// from cross-page links (McpKinds always-confirm chip, etc.). Selecting
// the tab is enough to surface the MCP section within Advanced; the
// scroll attribute lets us jump to a specific anchor.
const route = useRoute()
const router = useRouter()
const SIMPLE_PANEL_FOR_TAB: Record<string, string> = {
  general: 'simple-general',
  update: 'simple-update',
  notifications: 'simple-notifications',
  mcp: 'simple-mcp',
  account: 'simple-account',
  about: 'simple-about',
}

// Set when a link asks for an Advanced-only section while in Simple mode.
const hiddenTabRequested = ref<string | null>(null)

function openAdvancedTab(tab: string): void {
  hiddenTabRequested.value = null
  uiModeStore.setUiMode('advanced')
  activeTab.value = tab
}

function applyDeepLink(): void {
  // The page stays alive under other routes; their ?tab= is not ours.
  if (route.path !== '/settings') return
  if (route.query.tab === 'databases') {
    void router.replace('/databases')
    return
  }
  const scrollTarget = typeof route.query.scroll === 'string' ? route.query.scroll : ''
  const tabParam = scrollTarget === 'mcp-section'
    ? 'mcp'
    : (typeof route.query.tab === 'string' ? route.query.tab : '')
  if (tabParam && settingsSections.value.some(s => s.name === tabParam)) {
    if (uiModeStore.isSimple) {
      hiddenTabRequested.value = ADVANCED_ONLY_TABS.has(tabParam) ? tabParam : null
      // An Advanced-only section has no panel here; show the banner that says
      // so (the page keeps its scroll position between visits).
      const panel = hiddenTabRequested.value ? 'simple-hidden-tab' : SIMPLE_PANEL_FOR_TAB[tabParam]
      if (panel) setTimeout(() => document.getElementById(panel)?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 100)
      if (hiddenTabRequested.value) return
    } else {
      hiddenTabRequested.value = null
    }
    activeTab.value = tabParam
  }
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

// Keep the open section in the URL so reload and Back return to it.
watch(activeTab, (tab) => {
  if (route.path !== '/settings' || route.query.tab === tab) return
  void router.replace({ query: { ...route.query, tab, scroll: undefined } })
})

const saving = ref(false)
const systemInfo = ref<SystemInfo | null>(null)
const installedVersions = ref<Array<{ app: string; version: string }>>([])

const ports = reactive({
  http: 80,
  https: 443,
  mysql: 3306,
  redis: 6379,
  mailpitSmtp: 1025,
  mailpitHttp: 8025,
  postgresql: 5432,
})

// Two services on one port means one of them fails to start.
const duplicatePorts = computed<number[]>(() => {
  const values = Object.values(ports)
  return [...new Set(values.filter((port, i) => values.indexOf(port) !== i))]
})

const runOnStartup = ref(false)
// Phase 6.23 — MCP integration toggle. Default false; mirrors daemon's
// own `mcp.enabled` setting. When false, sidebar entry, banner, and
// /api/mcp/intents endpoints are all hidden/404.
const featureFlags = useFeatureFlagsStore()
const mcpEnabled = computed(() => featureFlags.mcpEnabled)
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
  saveAlwaysConfirmKinds()
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
  saveAlwaysConfirmKinds()
}

// MCP options save on change, like the capability toggles beside them.
async function saveMcpSetting(patch: Record<string, string>): Promise<boolean> {
  try {
    await saveSettings(patch)
    ElMessage.success(t('settingsToast.settingsSaved'))
    return true
  } catch (e) {
    ElMessage.error(t('settingsToast.saveFailed', { err: errorMessage(e) }))
    return false
  }
}

async function setMcpStrictKinds(strict: boolean) {
  if (await saveMcpSetting({ 'mcp.strict_kinds': String(strict) })) mcpStrictKinds.value = strict
}

function saveAlwaysConfirmKinds(): void {
  void saveMcpSetting({ 'mcp.always_confirm_kinds': mcpAlwaysConfirmKinds.value })
}

function saveMcpNumber(key: string, value: number | undefined): void {
  if (typeof value === 'number' && Number.isFinite(value)) void saveMcpSetting({ [key]: String(value) })
}

// The kinds list exists only while MCP is on (404 otherwise).
watch(mcpEnabled, (on) => {
  if (on && mcpKindOptions.value.length === 0) void loadMcpKindOptions()
}, { immediate: true })
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
// Iter 13: also store mode + pluginVersion for the backend status line.
const deployFlipUnlocked = ref<boolean>(false)
const deployFlipBlockers = ref<string[]>([])
interface DeployBlockerDetail { summary: string; phase: string; remediation: string }
const deployFlipBlockerDetails = ref<DeployBlockerDetail[]>([])
const deployBackendMode = ref<'built-in' | 'plugin' | null>(null)
const deployPluginVersion = ref<string | null>(null)
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
      deployRestartPending.value = j.restartPending === true
      deployFlipGatedEndpoints.value = Array.isArray(j.gatedEndpoints) ? j.gatedEndpoints : []
    }
  } catch { /* keep locked on error */ }
}

// Auto-start is now per-plugin only — toggle lives on each plugin card in
// Plugin Manager. The daemon still reads `service.<id>.autoStart` the same
// way, so the settings key format hasn't changed — just the UI surface.
const defaultPhp = ref('')
const phpVersions = ref<string[]>([])
const flushingDns = ref(false)
const mampDiscovering = ref(false)

// Simple-mode Services panel: a service whose binary is not on disk is
// "not installed" (grey), not a red failure.
function serviceIsRunning(svc: { state?: number; status?: string }): boolean {
  return svc.state === 2 || svc.status === 'running'
}
function serviceNotInstalled(svc: { installed?: boolean; state?: number; status?: string }): boolean {
  return !serviceIsRunning(svc) && svc.installed === false
}

// "0.5.0-692b611d+692b611dbc3a…": the part after "+" is build metadata,
// a full commit hash that only stretched the About table.
function shortVersion(version: string): string {
  return version.split('+')[0]
}

// Not-installed services are neither running nor a fault; the simple status
// bar says "all running" when every installed one runs.
const installedServiceCount = computed(() =>
  daemonStore.visibleServices.filter(s => s.installed !== false).length)

function revealDataDir(): void {
  const dir = systemInfo.value?.dataDir
  if (dir) void window.electronAPI?.revealInFolder?.(dir)
}
function serviceDot(svc: { installed?: boolean; state?: number; status?: string }): 'ok' | 'err' | 'muted' {
  if (serviceIsRunning(svc)) return 'ok'
  if (svc.state === 4 || svc.status === 'crashed' || svc.status === 'error') return 'err'
  return 'muted'
}
function serviceStateLabel(svc: { installed?: boolean; state?: number; status?: string }): string {
  if (serviceIsRunning(svc)) return t('common.running')
  if (serviceNotInstalled(svc)) return t('settings.simple.services.notInstalled')
  if (svc.state === 4 || svc.status === 'crashed' || svc.status === 'error') return t('settings.simple.services.crashed')
  return t('common.stopped')
}

// F85: daemon uptime in the UI language ("2 h 5 min", "3 dny 4 h").
function formatUptime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '—'
  const s = Math.floor(seconds)
  if (s < 60) return t('settings.about.uptime.seconds', { s })
  const m = Math.floor(s / 60)
  if (m < 60) return t('settings.about.uptime.minutes', { m, s: s % 60 })
  const h = Math.floor(m / 60)
  if (h < 24) return t('settings.about.uptime.hours', { h, m: m % 60 })
  const d = Math.floor(h / 24)
  return t('settings.about.uptime.days', { d, h: h % 24 }, d)
}

// ── Additional settings from SPEC ─────────────────────────────────────
const phpFpmBasePort = ref(9000)
const telemetryEnabled = ref(false)
const telemetryCrashReports = ref(false)
// Save writes every key; never let it run on top of a failed load.
const settingsLoaded = ref(false)

// Backup management
import { createBackup, listMcpKinds } from '../../api/daemon'
const backupCreating = ref(false)

async function manualBackup() {
  backupCreating.value = true
  try {
    const result = await createBackup()
    const missing = [...new Set((result.skipped ?? []).map(s => backupContentLabel(s.content)))]
    if (missing.length) ElMessage.warning(t('settingsToast.backupIncomplete', { missing: missing.join(', ') }))
    else ElMessage.success(t('settingsToast.backupCreated', { files: result.files, size: formatBytes(result.size, locale.value) }, result.files))
  } catch (e) {
    ElMessage.error(t('settingsToast.backupFailed', { err: errorMessage(e) }))
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

// ── Danger-zone reset operations ──────────────────────────────────────
const resettingSettings = ref(false)
const resettingFactory = ref(false)
// Plan §5/486 — danger zone behind progressive disclosure. Default
// collapsed so destructive actions require explicit reveal.
const dangerZoneExpanded = ref<string[]>([])

// Shared reset runner: POST the scope, surface HTTP/JSON errors clearly,
// then force a full window reload once the daemon-respawn has had time to
// pick a fresh port. Without the reload the user stays on a stale view —
// sites list still shows cached rows, toast flashes briefly, "vůbec nic
// neudělalo" impression. 3.5 s is the upper bound of our observed
// exit-99 → port-file → `/healthz` bounce window.
async function doReset(scope: 'settings' | 'factory'): Promise<boolean> {
  const url = `${daemonBaseUrl()}/api/admin/reset?scope=${scope}`
  const r = await fetch(url, { method: 'POST', headers: authHeaders() })
  if (!r.ok) {
    const body = await r.text().catch(() => '')
    throw new Error(body || `HTTP ${r.status}`)
  }
  ElMessage.success({ message: t('settings.danger.restarting'), duration: 3500 })
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
    await ElMessageBox.confirm(t('settings.danger.resetConfirm'), t('settings.danger.resetSettings'), {
      type: 'warning',
      confirmButtonText: t('settings.danger.resetConfirmButton'),
      cancelButtonText: t('common.cancel'),
    })
  } catch { return }
  resettingSettings.value = true
  try {
    await doReset('settings')
  } catch (e) {
    ElMessage.error(t('settingsToast.resetFailed', { err: errorMessage(e) }))
  } finally {
    resettingSettings.value = false
  }
}

async function confirmFactoryReset() {
  try {
    await ElMessageBox.confirm(t('settings.danger.factoryConfirm'), t('settings.danger.factoryReset'), {
      type: 'error',
      confirmButtonText: t('settings.danger.factoryConfirmButton'),
      cancelButtonText: t('common.cancel'),
      distinguishCancelAndClose: true,
    })
  } catch { return }
  resettingFactory.value = true
  try {
    await doReset('factory')
  } catch (e) {
    ElMessage.error(t('settingsToast.resetFailed', { err: errorMessage(e) }))
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
    ElMessage.success(t('settingsToast.mysqlRootSaved'))
  } catch (e) {
    ElMessage.error(t('settingsToast.saveFailed', { err: errorMessage(e) }))
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
  return isNaN(d.getTime()) ? String(s) : d.toLocaleString(locale.value)
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
    ElMessage.error(t('settingsToast.loadSnapshotsFailed', { err: errorMessage(e) }))
  } finally {
    snapshotsLoading.value = false
  }
}

async function restoreSnapshot(row: SyncSnapshot) {
  try {
    await ElMessageBox.confirm(
      t('settings.sync.snapshotRestoreConfirm', { date: formatDate(row.created_at) }),
      t('settings.sync.snapshotRestoreTitle'),
      { type: 'warning', confirmButtonText: t('common.restore'), cancelButtonText: t('common.cancel') },
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
      const portable = pickSyncableSettings(data.payload.settings as Record<string, string>)
      if (Object.keys(portable).length > 0) await saveSettings(portable)
      await loadSettings()
    }
    ElMessage.success(t('settingsToast.snapshotRestored'))
  } catch (e) {
    ElMessage.error(t('settingsToast.restoreFailed', { err: errorMessage(e) }))
  } finally {
    snapshotAction.value = null
  }
}

async function deleteSnapshot(row: SyncSnapshot) {
  try {
    await ElMessageBox.confirm(
      t('settings.sync.snapshotDeleteConfirm', { date: formatDate(row.created_at) }),
      t('settings.sync.snapshotDeleteTitle'),
      { type: 'warning', confirmButtonText: t('common.delete'), cancelButtonText: t('common.cancel') },
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
    ElMessage.success(t('settingsToast.snapshotDeleted'))
  } catch (e) {
    ElMessage.error(t('settingsToast.deleteFailed', { err: errorMessage(e) }))
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

const refreshingCatalog = ref(false)
const testingCatalog = ref(false)
const catalogStatus = ref<{ ok: boolean; message: string } | null>(null)

async function flushDns() {
  flushingDns.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/dns/flush`, { method: 'POST', headers: authHeaders() })
    // The daemon answers 200 either way; `ok` says whether ipconfig succeeded.
    const result = await r.json().catch(() => null) as { ok?: boolean; output?: string } | null
    if (r.ok && result?.ok) ElMessage.success(t('settingsToast.dnsFlushed'))
    else ElMessage.warning(t('settingsToast.dnsFlushFailed', { err: result?.output || `HTTP ${r.status}` }))
  } catch {
    ElMessage.warning(t('settingsToast.dnsFlushUnavailable'))
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
      ElMessage.info(t('settingsToast.noMampSites'))
      return
    }
    const confirmed = await ElMessageBox.confirm(
      t('settings.general.mampConfirm', {
        count: data.count,
        sites: (data.sites ?? []).map(s => s.domain || s.Domain).join(', '),
      }),
      t('settings.general.mampImport'),
      { confirmButtonText: t('settings.general.mampImportButton'), cancelButtonText: t('common.cancel'), type: 'info' },
    )
    if (confirmed) {
      const ir = await fetch(`${daemonBaseUrl()}/api/sites/migrate-mamp`, { method: 'POST', headers: authHeaders() })
      if (!ir.ok) throw new Error((await ir.text().catch(() => '')) || `HTTP ${ir.status}`)
      const result = await ir.json()
      ElMessage.success(t('settingsToast.mampImported', { count: result.count }, result.count))
    }
  } catch (e) {
    if (e !== 'cancel') ElMessage.error(t('settingsToast.mampFailed', { err: errorMessage(e) }))
  } finally {
    mampDiscovering.value = false
  }
}

// "Výchozí verze PHP" offers installed versions only. A stored version that
// is no longer installed is not shown; the effective default then is the
// active version (what the new-site dialog falls back to), else the first.
async function loadPhpVersions() {
  try {
    const [installed, settings] = await Promise.all([fetchPhpVersions(), fetchSettings()])
    phpVersions.value = installed.map(v => v.majorMinor || v.version)
    const stored = settings['general.defaultPhp'] ?? ''
    const active = installed.find(v => v.isActive)
    defaultPhp.value = phpVersions.value.includes(stored)
      ? stored
      : (active ? (active.majorMinor || active.version) : (phpVersions.value[0] ?? ''))
  } catch {
    phpVersions.value = []
    defaultPhp.value = ''
  }
}

async function saveDefaultPhp(version: string) {
  const previous = defaultPhp.value
  defaultPhp.value = version
  try {
    await saveSettings({ 'general.defaultPhp': version })
    ElMessage.success(t('settingsToast.settingsSaved'))
  } catch (e) {
    defaultPhp.value = previous
    ElMessage.error(t('settingsToast.saveFailed', { err: errorMessage(e) }))
  }
}

// "Spustit při startu" is the OS login item, read from and written to
// Electron directly — a value in the settings table started nothing.
const runOnStartupSupported = ref(false)
async function loadRunOnStartup() {
  const state = await window.electronAPI?.autostartGet?.().catch(() => null)
  runOnStartupSupported.value = state?.supported ?? false
  runOnStartup.value = state?.enabled ?? false
}

async function setRunOnStartup(enabled: boolean) {
  const api = window.electronAPI
  if (!api?.autostartSet) return
  try {
    const state = await api.autostartSet(enabled)
    runOnStartup.value = state.enabled
    if (state.enabled === enabled) ElMessage.success(t('settingsToast.settingsSaved'))
    else ElMessage.error(t('settingsToast.autostartFailed'))
  } catch (e) {
    ElMessage.error(t('settingsToast.saveFailed', { err: errorMessage(e) }))
  }
}

// Telemetry consent lives in the daemon's telemetry-consent.json, the file
// the main process checks before sending any crash report.
async function loadTelemetryConsent() {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/telemetry/consent`, { headers: authHeaders() })
    if (!r.ok) return
    const consent = await r.json() as { enabled?: boolean; crashReports?: boolean }
    telemetryEnabled.value = consent.enabled === true
    telemetryCrashReports.value = consent.crashReports === true
  } catch { /* daemon unreachable — switches stay off */ }
}

async function setTelemetryConsent(patch: { enabled?: boolean; crashReports?: boolean }) {
  const enabled = patch.enabled ?? telemetryEnabled.value
  const crashReports = patch.crashReports ?? telemetryCrashReports.value
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/telemetry/consent`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ enabled, crashReports, usageMetrics: false }),
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const consent = await r.json() as { enabled?: boolean; crashReports?: boolean }
    telemetryEnabled.value = consent.enabled === true
    telemetryCrashReports.value = consent.crashReports === true
    const state = await window.electronAPI?.telemetryConsentChanged?.().catch(() => null)
    ElMessage.success(state && state.consent && !state.active
      ? t('settingsToast.crashReportsAfterRestart')
      : t('settingsToast.settingsSaved'))
  } catch (e) {
    ElMessage.error(t('settingsToast.saveFailed', { err: errorMessage(e) }))
  }
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

const intOr = (raw: string | undefined, fallback: number): number => {
  const n = parseInt(raw ?? '')
  return Number.isFinite(n) ? n : fallback
}
const boolOn = (raw: string | undefined): boolean => raw === 'true' || raw === '1'
// deploy.* default to on; only an explicit "false"/"0" turns them off.
const boolDefaultOn = (raw: string | undefined): boolean => !(raw === 'false' || raw === '0')

/** Puts daemon settings (flat "category.key" strings) into the form. */
function applyValues(data: Record<string, string>): void {
  ports.http = intOr(data['ports.http'], 80)
  ports.https = intOr(data['ports.https'], 443)
  ports.mysql = intOr(data['ports.mysql'], 3306)
  ports.redis = intOr(data['ports.redis'], 6379)
  ports.mailpitSmtp = intOr(data['ports.mailpitSmtp'], 1025)
  ports.mailpitHttp = intOr(data['ports.mailpitHttp'], 8025)
  ports.postgresql = intOr(data['ports.postgresql'], 5432)
  phpFpmBasePort.value = intOr(data['ports.phpFpmBase'], 9000)
  mcpStrictKinds.value = boolOn(data['mcp.strict_kinds'])
  mcpAlwaysConfirmKinds.value = data['mcp.always_confirm_kinds'] ?? ''
  // Retention defaults match GrantSweeperService (1 d / 30 d) and the
  // tool-call sweeper (30 d); 0 disables the grant sweeps.
  mcpExpiredRetentionDays.value = Math.max(0, intOr(data['mcp.grant_expired_retention_days'], 1))
  mcpRevokedRetentionDays.value = Math.max(0, intOr(data['mcp.grant_revoked_retention_days'], 30))
  mcpToolCallRetentionDays.value = Math.max(1, intOr(data['mcp.toolCallRetentionDays'], 30))
  deployEnabled.value = boolDefaultOn(data['deploy.enabled'])
  deployUseLegacyHostHandlers.value = boolDefaultOn(data['deploy.useLegacyHostHandlers'])
  catalogUrl.value = data['daemon.catalogUrl'] ?? ''
  pluginAutoSync.value = data['plugins.autoSyncEnabled'] === 'true'
  deviceName.value = data['sync.deviceName'] ?? ''
}

/** The form as the daemon stores it — the counterpart of applyValues(). */
function formValues(): Record<string, string> {
  return {
    'ports.http': String(ports.http),
    'ports.https': String(ports.https),
    'ports.mysql': String(ports.mysql),
    'ports.redis': String(ports.redis),
    'ports.mailpitSmtp': String(ports.mailpitSmtp),
    'ports.mailpitHttp': String(ports.mailpitHttp),
    'ports.postgresql': String(ports.postgresql),
    'ports.phpFpmBase': String(phpFpmBasePort.value),
    'deploy.enabled': String(deployEnabled.value),
    'deploy.useLegacyHostHandlers': String(deployUseLegacyHostHandlers.value),
    'daemon.catalogUrl': catalogUrl.value,
    'plugins.autoSyncEnabled': String(pluginAutoSync.value),
    'sync.deviceName': deviceName.value,
  }
}

// What the daemon held when the form was last loaded or saved. Save sends
// only the keys that differ from it: every port key in a batch used to make
// the daemon regenerate vhosts and reload services, and this page stays
// alive in the background, so re-sending everything also wrote back stale
// values changed elsewhere meanwhile — mcp.enabled from the MCP page.
const loadedValues = ref<Record<string, string>>({})
const changedValues = computed<Record<string, string>>(() =>
  Object.fromEntries(Object.entries(formValues()).filter(([key, value]) => loadedValues.value[key] !== value)))
const changedCount = computed(() => Object.keys(changedValues.value).length)
const isDirty = computed(() => settingsLoaded.value && changedCount.value > 0)

onBeforeRouteLeave(async () => {
  if (!isDirty.value) return true
  try {
    await ElMessageBox.confirm(t('settings.unsaved.leaveMessage'), t('settings.unsaved.leaveTitle'), {
      type: 'warning',
      confirmButtonText: t('settings.unsaved.leave'),
      cancelButtonText: t('settings.unsaved.stay'),
    })
  } catch {
    return false
  }
  discardChanges()
  return true
})

// The page is kept alive between visits; refresh what other pages may have
// changed meanwhile, unless the user is in the middle of editing.
onActivated(() => {
  if (settingsLoaded.value && !isDirty.value) void loadSettings()
})

async function loadSettings() {
  try {
    applyValues(await fetchSettings())
    loadedValues.value = formValues()
    settingsLoaded.value = true
  } catch { /* daemon not reachable — keep defaults, saving stays blocked */ }
}

function discardChanges(): void {
  applyValues(loadedValues.value)
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
    const msg = t('settings.advanced.pluginSyncResult', { count: result.catalogCount ?? 0, installed: result.installedThisCall ?? 0 })
    pluginSyncStatus.value = { ok: true, message: msg }
    ElMessage.success(msg)
    // Re-poll the status endpoint so the "last sync" label reflects the
    // freshly-completed refresh without a tab navigation.
    await loadPluginCatalogStatus()
  } catch (e) {
    const msg = errorMessage(e)
    pluginSyncStatus.value = { ok: false, message: msg }
    ElMessage.error(t('settingsToast.pluginSyncFailed', { err: msg }))
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
    loadedValues.value = { ...loadedValues.value, 'daemon.catalogUrl': catalogUrl.value || '' }
    const r = await fetch(`${daemonBaseUrl()}/api/binaries/catalog/refresh`, {
      method: 'POST',
      headers: authHeaders(),
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const result = await r.json()
    catalogStatus.value = { ok: true, message: t('settingsToast.catalogRefreshed', { count: result.count ?? 0 }) }
    ElMessage.success(t('settingsToast.catalogRefreshed', { count: result.count ?? 0 }))
  } catch (e) {
    const msg = errorMessage(e)
    catalogStatus.value = { ok: false, message: t('settingsToast.refreshFailed', { err: msg }) }
    ElMessage.error(t('settingsToast.refreshFailed', { err: msg }))
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
      message: t('settings.advanced.catalogReachable', { service: body.service ?? 'catalog-api', version: body.version ?? '?' }),
    }
  } catch (e) {
    catalogStatus.value = { ok: false, message: t('settings.advanced.catalogUnreachable', { err: errorMessage(e) }) }
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
      t('settings.devices.unlinkConfirm', { name: row.name || row.device_id.slice(0, 12) }),
      t('settings.devices.unlinkTitle'),
      { type: 'warning', confirmButtonText: t('settings.devices.unlink'), cancelButtonText: t('common.cancel') },
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
    ElMessage.success(t('settingsToast.deviceUnlinked'))
    await loadDevicesAccount()
  } catch (e) {
    ElMessage.error(t('settingsToast.unlinkFailed', { err: errorMessage(e) }))
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
    ElMessage.success(t('settingsToast.deviceRenamed'))
  } catch (e) {
    ElMessage.error(t('settingsToast.renameFailed', { err: errorMessage(e) }))
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
    ElMessage.success(t('settingsToast.signedInAs', { email: result.email }))

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
    ElMessage.success(t('settingsToast.accountCreated', { email: result.email }))
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
  ElMessage.success(t('settingsToast.signedOut'))
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
    ElMessage.error(t('settingsToast.loadDevicesFailed', { err: msg }))
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
  const target = accountDevices.value.find(d => d.device_id === targetDeviceId)
  try {
    await ElMessageBox.confirm(
      t('settings.devices.pushConfirm', { name: target?.name || targetDeviceId.slice(0, 12) }),
      t('settings.devices.pushTitle'),
      { type: 'warning', confirmButtonText: t('settings.devices.pushHere'), cancelButtonText: t('common.cancel') },
    )
  } catch { return }
  pushingTo.value = targetDeviceId
  try {
    await pushConfigToDevice(getCatalogUrl(), accountToken.value, targetDeviceId, deviceId.value)
    ElMessage.success(t('settingsToast.configPushedToDevice', { id: targetDeviceId.slice(0, 8) }))
  } catch (e) {
    ElMessage.error(t('settingsToast.pushFailed', { err: errorMessage(e) }))
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
    if (data['sync.lastSyncTime']) lastSyncTime.value = data['sync.lastSyncTime']
  } catch { /* daemon not reachable */ }
}

function copyDeviceId() {
  navigator.clipboard.writeText(deviceId.value)
    .then(() => ElMessage.success(t('settingsToast.deviceIdCopied')))
    .catch(() => ElMessage.warning(t('settingsToast.cannotAccessClipboard')))
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

  const settings = pickSyncableSettings(rawSettings)

  // Filter sites: keep only SITE_SYNC_FIELDS per site (plus domain as the key)
  const sites = rawSites.map(site => {
    const filtered: Record<string, unknown> = { domain: site.domain }
    for (const field of SITE_SYNC_FIELDS) {
      if (field in site) filtered[field] = site[field]
    }
    return filtered
  })

  // No deviceId here: the push request names the device separately, and an
  // exported file must not carry this machine's identity to another one.
  return {
    exportedAt: new Date().toISOString(),
    version: appVersion.value,
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
    loadedValues.value = { ...loadedValues.value, 'sync.deviceName': deviceName.value }

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
    syncStatus.value = { ok: true, message: t('settings.sync.status.pushed') }
    ElMessage.success(t('settingsToast.configPushedToCloud'))
  } catch (e) {
    const msg = errorMessage(e)
    syncStatus.value = { ok: false, message: t('settingsToast.pushFailed', { err: msg }) }
    ElMessage.error(t('settingsToast.pushFailed', { err: msg }))
  } finally {
    syncing.value = false
  }
}

// Settings classification lives in utils/settingsSync (an allowlist). Site
// fields follow the same principle: domain/phpVersion/ssl/aliases/framework/
// cloudflare are portable; documentRoot/ports stay local.
const SITE_SYNC_FIELDS = new Set([
  'domain', 'phpVersion', 'sslEnabled', 'aliases', 'framework',
  'environment', 'cloudflare', 'nodeUpstreamPort', 'nodeStartCommand',
])
// Note: the inverse set (documentRoot/httpPort/httpsPort) is implicitly
// excluded by iterating SITE_SYNC_FIELDS only — no need for a second set.

async function pullFromCloud() {
  try {
    await ElMessageBox.confirm(t('settings.sync.pullConfirm'), t('settings.sync.pullFromCloud'), {
      type: 'warning',
      confirmButtonText: t('settings.sync.pullFromCloud'),
      cancelButtonText: t('common.cancel'),
    })
  } catch { return }
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
        syncStatus.value = { ok: false, message: t('settings.sync.status.noSnapshot') }
        ElMessage.info(t('settingsToast.noCloudSnapshot'))
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
      const merged = pickSyncableSettings(payload.settings as Record<string, string>)
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
        ElMessage.info(t('settingsToast.newSitesImported', { count: newSiteCount }))
      }
    }

    syncStatus.value = { ok: true, message: t('settings.sync.status.pulled', { date: formatDate(data.updated_at) }) }
    ElMessage.success(t('settingsToast.configSynced'))
    await loadSettings()
  } catch (e) {
    const msg = errorMessage(e)
    syncStatus.value = { ok: false, message: t('settingsToast.pullFailed', { err: msg }) }
    ElMessage.error(t('settingsToast.pullFailed', { err: msg }))
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
        ? t('settings.sync.status.exists', { date: formatDate(data.updated_at) })
        : t('settings.sync.status.noSnapshot'),
    }
  } catch (e) {
    syncStatus.value = { ok: false, message: t('settings.sync.status.checkFailed', { err: errorMessage(e) }) }
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
    ElMessage.success(t('settingsToast.settingsExported'))
  } catch (e) {
    ElMessage.error(t('settingsToast.exportFailed', { err: errorMessage(e) }))
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
      throw new Error(t('settings.sync.importInvalid'))
    }
    await ElMessageBox.confirm(
      t('settings.sync.importConfirm', {
        file: file.name,
        device: data.deviceName || '—',
        date: data.exportedAt ? new Date(data.exportedAt).toLocaleString(locale.value) : '—',
      }),
      t('settings.sync.importTitle'),
      { confirmButtonText: t('settings.sync.importFile'), cancelButtonText: t('common.cancel'), type: 'warning' },
    )

    // Same allowlist as pullFromCloud: a foreign file must not replace the
    // catalog login or this device's identity.
    const merged = pickSyncableSettings(data.settings as Record<string, string>)
    if (Object.keys(merged).length > 0) {
      await saveSettings(merged)
    }
    ElMessage.success(t('settingsToast.syncImported', { count: Object.keys(merged).length, file: file.name }))
    await loadSettings()
  } catch (e) {
    if (e !== 'cancel') ElMessage.error(t('settingsToast.importFailed', { err: errorMessage(e) }))
  }
  input.value = ''
}

// ── Update check ──────────────────────────────────────────────────────
// Two sources: GitHub's latest release for the version on offer and its
// notes, and electron-updater in the main process, which downloads and
// installs. Portable and dev builds have no updater and link to the
// release page instead.
const currentVersion = appVersion

const updateCheck = reactive<EasyUpdateCheck>({
  loading: false,
  latest: null,
  lastCheckedIso: localStorage.getItem('wdc-last-update-check'),
  error: null,
  releaseNotes: null,
  releaseUrl: null,
})
const updaterState = ref<AppUpdaterState | null>(null)
let unsubscribeUpdater: (() => void) | null = null

// electron-updater checks on its own at startup. Until the GitHub check
// runs, the version its feed reported is the newest one known, also after
// a failed download (an error keeps the last version the feed reported).
const latestVersion = computed<string | null>(() => {
  if (updateCheck.latest) return updateCheck.latest
  const u = updaterState.value
  return u && (u.status === 'up-to-date' || u.status === 'downloading' || u.status === 'downloaded' || u.status === 'error') ? u.version : null
})
const lastCheckedIso = computed<string | null>(() =>
  [updateCheck.lastCheckedIso, updaterState.value?.checkedAt].filter((x): x is string => !!x).sort().pop() ?? null)
const hasUpdate = computed(() => !!latestVersion.value
  && compareSemver(versionCore(latestVersion.value), versionCore(currentVersion.value)) > 0)

async function runUpdateCheck() {
  updateCheck.loading = true
  updateCheck.error = null
  if (updaterState.value && updaterState.value.status !== 'disabled') {
    void window.electronAPI?.updaterCheck?.()
  }
  try {
    const r = await fetch('https://api.github.com/repos/nks-hub/webdev-console/releases/latest')
    // Unauthenticated GitHub API allows 60 requests an hour per IP.
    if (r.status === 403 || r.status === 429) throw new Error(t('settings.update.rateLimited'))
    if (!r.ok) throw new Error(t('settings.update.checkFailed', { status: r.status }))
    const data = await r.json() as { tag_name?: string; html_url?: string; body?: string }
    updateCheck.latest = (data.tag_name ?? '').replace(/^v/, '') || null
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

async function installUpdate() {
  await window.electronAPI?.updaterInstall?.()
}

function openReleasePage() {
  const url = updateCheck.releaseUrl || 'https://github.com/nks-hub/webdev-console/releases/latest'
  if (window.electronAPI?.openExternal) void window.electronAPI.openExternal(url)
  else window.open(url, '_blank')
}

// Simple-mode panel subtitle: the current state in a few words.
const updateSubtitle = computed(() => {
  const u = updaterState.value
  if (u?.status === 'downloaded') return t('settings.update.state.downloaded', { v: u.version ?? '' })
  if (u?.status === 'downloading') return t('settings.update.state.downloading', { p: u.percent ?? 0 })
  if (hasUpdate.value) return t('settings.update.state.available', { v: latestVersion.value ?? '' })
  if (latestVersion.value) return t('settings.update.upToDate')
  return ''
})

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

const relativeTime = computed(() => new Intl.RelativeTimeFormat(locale.value, { numeric: 'auto' }))
/** "před 5 minutami" / "5 minutes ago" in the UI language. */
function formatRelativeTime(iso: string): string {
  const minutes = Math.round((new Date(iso).getTime() - Date.now()) / 60_000)
  if (Math.abs(minutes) < 60) return relativeTime.value.format(minutes, 'minute')
  const hours = Math.round(minutes / 60)
  if (Math.abs(hours) < 24) return relativeTime.value.format(hours, 'hour')
  return relativeTime.value.format(Math.round(hours / 24), 'day')
}

// Iter 29: live-refresh readiness on deploy.* settings save from another
// tab. Mirrors the iter 28 wiring in DeploySettingsPanel — when operator
// flips useLegacyHostHandlers via API or another open Settings page, the
// locked-toggle popover reflects the new state without reload.
let unsubscribeDeploySettingsSse: (() => void) | null = null

onMounted(async () => {
  void loadSettings()
  void loadPhpVersions()
  void loadRunOnStartup()
  void window.electronAPI?.updaterGetStatus?.().then((state) => { updaterState.value = state }).catch(() => {})
  unsubscribeUpdater = window.electronAPI?.onUpdaterStatus?.((state) => { updaterState.value = state }) ?? null
  void loadTelemetryConsent()
  void loadDeviceId()
  void loadPluginCatalogStatus()
  void loadPluginPorts()
  void loadDeployFlipReadiness()
  unsubscribeDeploySettingsSse = subscribeEventsMap({
    'deploy:settings-changed': () => { void loadDeployFlipReadiness() },
  })
  void loadMysqlRootStatus()
  void loadSnapshots()
  if (accountToken.value) void loadDevicesAccount()
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
  unsubscribeUpdater?.()
  unsubscribeUpdater = null
  if (unsubscribeDeploySettingsSse) {
    unsubscribeDeploySettingsSse()
    unsubscribeDeploySettingsSse = null
  }
})

// New ports something else already listens on ("8080 (nginx)"). Only the
// changed ones: the current ports are held by our own running services.
async function portsInUse(patch: Record<string, string>): Promise<string[]> {
  const changed = Object.entries(patch)
    .filter(([key]) => key.startsWith('ports.') && key !== 'ports.phpFpmBase')
    .map(([, value]) => value)
  const results = await Promise.all(changed.map(async (port) => {
    try {
      const r = await fetch(`${daemonBaseUrl()}/api/ports/check?port=${encodeURIComponent(port)}`, { headers: authHeaders() })
      if (!r.ok) return null
      const info = await r.json() as { inUse?: boolean; ownerName?: string | null }
      return info.inUse ? (info.ownerName ? `${port} (${info.ownerName})` : port) : null
    } catch { return null }
  }))
  return results.filter((x): x is string => x !== null)
}

async function save() {
  if (!settingsLoaded.value) {
    ElMessage.error(t('settings.loadFailedSaveBlocked'))
    return
  }
  const patch = changedValues.value
  if (Object.keys(patch).length === 0) return
  if (duplicatePorts.value.length > 0) {
    ElMessage.error(t('settings.ports.duplicate', { ports: duplicatePorts.value.join(', ') }))
    return
  }
  const busy = await portsInUse(patch)
  if (busy.length > 0) {
    try {
      await ElMessageBox.confirm(t('settings.ports.inUseConfirm', { ports: busy.join(', ') }), t('settings.ports.inUseTitle'), {
        type: 'warning',
        confirmButtonText: t('settings.unsaved.save'),
        cancelButtonText: t('common.cancel'),
      })
    } catch { return }
  }
  saving.value = true
  try {
    // saveSettings throws on non-ok with the daemon's error message extracted.
    await saveSettings(patch)
    loadedValues.value = { ...loadedValues.value, ...patch }
    ElMessage.success(t('settingsToast.settingsSaved'))
  } catch (e) {
    ElMessage.error(t('settingsToast.failedToSave', { err: errorMessage(e) }))
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
  padding: 0 0 var(--wdc-space-4);
  margin-bottom: 0;
  border-bottom: 1px solid var(--wdc-border);
  background: transparent;
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
  margin-top: var(--wdc-space-1);
}

.page-body {
  padding: var(--wdc-space-5) 0;
}

.simple-settings-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.35fr) minmax(320px, 0.65fr);
  gap: var(--wdc-space-4);
  align-items: start;
}

/* Panel chrome moved to SettingsPanel shared primitive (plan §6).
   Only the panel-main grid-span modifier remains, plus markup-driven
   helpers (.simple-runtime-grid, .simple-settings-actions, footer). */
.simple-settings-panel-main {
  grid-row: span 2;
}

.simple-settings-actions {
  display: flex;
  gap: var(--wdc-space-2);
  margin-top: var(--wdc-space-2-5);
  flex-wrap: wrap;
}

.simple-runtime-grid {
  display: grid;
  gap: var(--wdc-space-1);
}

.settings-tabs {
  --el-tabs-header-height: 40px;
}

.tab-content {
  padding: var(--wdc-space-4-5) 0;
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(480px, 1fr));
  grid-auto-flow: dense;
  gap: var(--wdc-space-4-5) var(--wdc-space-5);
  align-items: start;
}
.tab-content > .tab-desc,
.tab-content > .settings-footer {
  grid-column: 1 / -1;
}
.tab-content > .danger-zone {
  grid-column: 1 / -1;
}

.tab-desc {
  font-size: 0.82rem;
  color: var(--el-text-color-secondary);
  margin-bottom: var(--wdc-space-4-5);
  line-height: 1.5;
}

.mono-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
}

.hint {
  margin-top: var(--wdc-space-2);
  font-size: 0.76rem;
  color: var(--wdc-text-3);
  line-height: 1.5;
}
.danger-zone {
  margin-top: var(--wdc-space-6);
  padding: var(--wdc-space-4-5) var(--wdc-space-4-5);
  background: color-mix(in srgb, var(--el-color-danger) 8%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-danger) 30%, transparent);
  border-radius: var(--wdc-radius-sm);
  max-width: none;
}
.danger-title {
  margin: 0 0 var(--wdc-space-1) 0;
  font-size: 0.95rem;
  font-weight: 700;
  color: var(--el-color-danger);
}
.danger-desc {
  margin: 0 0 var(--wdc-space-4-5) 0;
  font-size: 0.82rem;
  color: var(--wdc-text-3);
}
.danger-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-4);
  padding: var(--wdc-space-3) 0;
  border-top: 1px solid color-mix(in srgb, var(--el-color-danger) 20%, transparent);
}
.danger-row .danger-info {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-1);
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
  padding: 1px var(--wdc-space-1-5);
  border-radius: var(--wdc-radius-sm);
  color: var(--wdc-accent);
  font-size: 0.74rem;
}
/* .status-dot CSS removed — all 4 usages migrated to HealthStatusDot
   shared primitive (plan §6). */

.settings-footer {
  margin-top: var(--wdc-space-5);
  padding-top: var(--wdc-space-4);
  border-top: 1px solid var(--el-border-color);
  display: flex;
  gap: var(--wdc-space-2);
}

.footer-unsaved {
  align-self: center;
  margin-right: auto;
  font-size: 0.85rem;
  color: var(--wdc-text-2);
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
    padding: var(--wdc-space-4-5) var(--wdc-space-4) 0;
    margin-bottom: var(--wdc-space-3-5);
  }

  .page-body {
    padding: 0 var(--wdc-space-4) var(--wdc-space-4-5);
  }

  .simple-settings-panel {
    padding: var(--wdc-space-3-5);
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
  gap: var(--wdc-space-5-5) var(--wdc-space-6-5);
  padding: var(--wdc-space-2) var(--wdc-space-1);
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
  gap: var(--wdc-space-3);
  font-size: 1.4rem;
  font-weight: 800;
  letter-spacing: 0.04em;
  background: var(--wdc-brand-gradient);
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

.simple-hidden-tab {
  grid-column: 1 / -1;
}
.simple-hidden-tab :deep(.el-alert__description) {
  margin-top: var(--wdc-space-2);
}

.about-system { font-size: 0.85rem; }
.about-sys-title {
  font-size: 0.82rem;
  font-weight: 500;
  color: var(--wdc-text-3);
  margin: 0 0 var(--wdc-space-1-5);
  font-family: var(--wdc-font-display);
}
.about-sys-row {
  display: flex;
  justify-content: space-between;
  padding: var(--wdc-space-1) 0;
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
  gap: var(--wdc-space-2);
  width: 100%;
}
.mysql-root-row .el-input {
  flex: 1 1 0;
  min-width: 0;
}
.about-sso-status { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 6px; font-size: 0.78rem; color: var(--el-text-color-secondary); }
.about-sso-status .sys-label, .about-sso-status .sys-value { white-space: nowrap; }

/* Wrapped action buttons line up on the left; the gap does the spacing. */
.simple-settings-grid :deep(.settings-panel-actions .el-button + .el-button) { margin-left: 0; }
.paths-backup-actions { display: flex; gap: var(--wdc-space-2); flex-wrap: wrap; }
.mcp-tab { grid-template-columns: minmax(0, 1fr); }
/* Same reading width as the capability list above it. */
.mcp-tab > #mcp-section { max-width: 72ch; }
.port-input { width: 180px; }
.sync-sign-in { margin-bottom: var(--wdc-space-3); }
.sync-sign-in :deep(.el-alert__description) { margin-top: var(--wdc-space-2); }

/* Sync tab */
.settings-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  margin-bottom: var(--wdc-space-4);
  overflow: hidden;
}
.settings-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--wdc-space-3-5) var(--wdc-space-4-5);
  background: var(--wdc-surface-2);
  border-bottom: 1px solid var(--wdc-border);
}
.settings-card-title {
  font-size: 0.82rem;
  font-weight: 500;
  color: var(--wdc-text);
  font-family: var(--wdc-font-display);
}
.settings-card-body { padding: 18px; }
.sync-actions { display: flex; gap: 8px; flex-wrap: wrap; }

/* Update tab */
.mono { font-family: 'JetBrains Mono', monospace; font-size: 0.88rem; }
.text-muted { color: var(--wdc-text-3); font-size: 0.82rem; }
.update-actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; margin-top: 16px; }
</style>
