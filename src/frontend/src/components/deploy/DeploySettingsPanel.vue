<template>
  <div class="settings-panel">
    <!-- Phase 7.5+++ Settings redesign — sticky save bar at the top.
         Always visible while the operator scrolls; dirty indicator shows
         when there are pending changes; primary CTA saves all sections
         in one round-trip so individual section "Save" buttons aren't
         load-bearing anymore. -->
    <div class="settings-header">
      <div class="settings-header-left">
        <span class="settings-domain">{{ domain }}</span>
        <span class="settings-section-label">· {{ activeSectionLabel }}</span>
        <el-tag v-if="dirty" type="warning" size="small" effect="dark" class="dirty-tag">
          ● {{ t('deploySettings.unsavedChanges') }}
        </el-tag>
        <el-tag v-else type="success" size="small" effect="plain" class="dirty-tag">
          ✓ {{ t('deploySettings.allSaved') }}
        </el-tag>
        <!-- Phase 7.5+++ — MCP grants visibility per site. Surfaces how
             many active grants would let an AI fire destructive actions
             on this domain without confirmation. Click → /mcp/grants
             pre-filtered. Hidden when zero (no AI exposure on this site). -->
        <el-tooltip
          v-if="mcpGrantsCount > 0"
          :content="t('deploySettings.mcpGrantsTooltip', { n: mcpGrantsCount })"
          placement="bottom"
        >
          <el-tag
            type="info"
            size="small"
            effect="plain"
            class="mcp-grants-tag"
            @click="goToMcpGrants"
          >
            🔓 {{ t('deploySettings.mcpGrantsBadge', { n: mcpGrantsCount }) }}
          </el-tag>
        </el-tooltip>
      </div>
      <div class="settings-header-actions">
        <el-button :disabled="!dirty || saving" plain @click="discardChanges">
          {{ t('deploySettings.discard') }}
        </el-button>
        <el-button type="primary" :disabled="!dirty" :loading="saving" @click="saveSettings">
          {{ t('deploySettings.saveAll') }}
        </el-button>
      </div>
    </div>

    <el-tabs v-model="activeSection" tab-position="left" class="settings-tabs">

      <!-- ── A: Hosts ──────────────────────────────────────────── -->
      <el-tab-pane name="hosts" :label="t('deploySettings.tabs.hosts')">
        <div class="section-body">
          <div class="section-intro">
            <h2 class="section-h2">{{ t('deploySettings.hosts.title') }}</h2>
            <p class="section-lead">{{ t('deploySettings.hosts.lead') }}</p>
          </div>

          <!-- Empty state when no hosts configured: rich CTA panel. -->
          <div v-if="settings.hosts.length === 0" class="hosts-empty">
            <el-empty :image-size="100" :description="''">
              <template #description>
                <h3 class="empty-title">{{ t('deploySettings.hosts.emptyTitle') }}</h3>
                <p class="empty-lead">{{ t('deploySettings.hosts.empty') }}</p>
              </template>
              <el-button type="primary" size="large" @click="openAddHostModal">
                + {{ t('deploySettings.hosts.addFirstHost') }}
              </el-button>
            </el-empty>
          </div>

          <!-- Card grid when hosts exist — replaces the bare table. -->
          <div v-else>
            <div class="hosts-toolbar">
              <span class="hosts-count muted">
                {{ t('deploySettings.hosts.count', { n: settings.hosts.length }) }}
              </span>
              <el-button type="primary" size="default" @click="openAddHostModal">
                + {{ t('deploySettings.hosts.addHost') }}
              </el-button>
            </div>
            <div class="hosts-grid">
              <HostSettingsCard
                v-for="h in settings.hosts"
                :key="h.name"
                :host="h"
                @edit="openEditHostModal(h)"
                @remove="removeHost(h.name)"
              />
            </div>
          </div>
        </div>
      </el-tab-pane>

      <!-- ── B: Snapshots ──────────────────────────────────────── -->
      <el-tab-pane name="snapshots" :label="t('deploySettings.tabs.snapshots')">
        <div class="section-body">
          <h3 class="section-title">{{ t('deploySettings.snapshot.title') }}</h3>

          <el-form
            :model="settings.snapshot"
            label-position="top"
            size="default"
            class="settings-form"
          >
            <el-form-item>
              <template #label>
                <label :for="ids.snapshotEnabled">{{ t('deploySettings.snapshot.enabled') }}</label>
              </template>
              <el-switch
                :id="ids.snapshotEnabled"
                v-model="settings.snapshot.enabled"
                :active-text="t('deploySettings.snapshot.switchEnabled')"
                :inactive-text="t('deploySettings.snapshot.switchDisabled')"
                aria-describedby="snapshot-help"
              />
              <div id="snapshot-help" class="field-hint">
                {{ t('deploySettings.snapshot.enabledHint') }}
              </div>
            </el-form-item>

            <el-form-item v-if="settings.snapshot.enabled">
              <template #label>
                <label :for="ids.retentionDays">{{ t('deploySettings.snapshot.retentionDays') }}</label>
              </template>
              <el-input-number
                :id="ids.retentionDays"
                v-model="settings.snapshot.retentionDays"
                :min="1"
                :max="365"
                controls-position="right"
                aria-required="true"
                style="width: 140px"
              />
            </el-form-item>
          </el-form>

          <!-- Detection notice -->
          <el-alert
            type="info"
            :closable="false"
            show-icon
            class="snapshot-notice"
          >
            <template #title>
              {{ t('deploySettings.snapshot.detectionTitle') }}
            </template>
            {{ t('deploySettings.snapshot.detectionBody') }}
          </el-alert>

          <!-- On-demand snapshot trigger -->
          <div class="subsection">
            <div class="subsection-header">{{ t('deploySettings.snapshot.onDemandTitle') }}</div>
            <p class="muted">
              {{ t('deploySettings.snapshot.onDemandHint') }}
            </p>
            <el-button
              type="primary"
              :loading="snapshotting"
              :disabled="restoringId !== null"
              @click="onSnapshotNow"
            >
              {{ t('deploySettings.snapshot.snapshotNow') }}
            </el-button>
          </div>

          <!-- Phase 7.5+++ Snapshots timeline redesign — vertical chronological
               feed with kind badges + filter. Replaces the bare table for a
               more "audit log"-style view that's easier to scan. -->
          <div class="subsection">
            <div class="subsection-header snapshots-header">
              <span>{{ t('deploySettings.snapshot.recentTitle') }}</span>
              <el-radio-group
                v-if="snapshots.length > 0"
                v-model="snapshotsFilter"
                size="small"
                class="snapshots-filter"
              >
                <el-radio-button label="all">{{ t('deploySettings.snapshot.filterAll') }}</el-radio-button>
                <el-radio-button label="pre-deploy">{{ t('deploySettings.snapshot.filterPreDeploy') }}</el-radio-button>
                <el-radio-button label="manual">{{ t('deploySettings.snapshot.filterManual') }}</el-radio-button>
              </el-radio-group>
            </div>
            <div v-if="snapshots.length > 0" class="snapshots-summary muted">
              {{ t('deploySettings.snapshot.summaryCount', { n: snapshots.length }) }}
              ·
              {{ t('deploySettings.snapshot.summarySize', { size: formatBytes(snapshotsTotalBytes) }) }}
              <span v-if="snapshotsOldestAt" class="snapshots-oldest">
                · {{ t('deploySettings.snapshot.summaryOldest',
                       { when: formatDate(snapshotsOldestAt) }) }}
              </span>
            </div>
            <div v-if="snapshots.length === 0" class="muted">
              {{ t('deploySettings.snapshot.noneYet') }}
            </div>
            <div v-else-if="filteredSnapshots.length === 0" class="muted">
              {{ t('deploySettings.snapshot.filterNoMatch') }}
            </div>
            <ul v-else class="snapshots-timeline">
              <li v-for="snap in filteredSnapshots" :key="snap.id" class="timeline-item"
                  :class="`tl-${snapshotKind(snap.path)}`">
                <span class="timeline-dot"></span>
                <div class="timeline-content">
                  <div class="timeline-row">
                    <el-tag
                      :type="snapshotKind(snap.path) === 'pre-deploy' ? 'info' : 'warning'"
                      size="small" effect="plain"
                    >
                      {{ snapshotKind(snap.path) === 'pre-deploy'
                          ? t('deploySettings.snapshot.kindPreDeploy')
                          : t('deploySettings.snapshot.kindManual') }}
                    </el-tag>
                    <span class="timeline-when">{{ formatDate(snap.createdAt) }}</span>
                    <span class="muted">· {{ formatBytes(snap.sizeBytes) }}</span>
                    <el-button
                      size="small"
                      type="danger"
                      plain
                      class="timeline-restore"
                      :loading="restoringId === snap.id"
                      :disabled="(restoringId !== null && restoringId !== snap.id) || snapshotting"
                      :aria-label="t('deploySettings.snapshot.restoreTitle')"
                      @click="onRestoreSnapshot(snap)"
                    >
                      {{ t('deploySettings.snapshot.restore') }}
                    </el-button>
                  </div>
                  <div class="timeline-path mono muted">{{ snap.path }}</div>
                </div>
              </li>
            </ul>
          </div>

          <div class="section-footer">
            <el-button type="primary" :loading="saving" @click="saveSettings">
              {{ t('deploySettings.snapshot.saveSettings') }}
            </el-button>
          </div>
        </div>
      </el-tab-pane>

      <!-- ── C: Hooks (redesign #155 — card per hook) ─────────────── -->
      <el-tab-pane name="hooks" :label="t('deploySettings.tabs.hooks')">
        <div class="section-body">
          <div class="section-intro">
            <h2 class="section-h2">{{ t('deploySettings.hooks.title') }}</h2>
            <p class="section-lead">{{ t('deploySettings.hooks.intro') }}</p>
          </div>

          <div v-if="settings.hooks.length === 0" class="hosts-empty">
            <el-empty :image-size="80" :description="''">
              <template #description>
                <h3 class="empty-title">{{ t('deploySettings.hooks.emptyTitle') }}</h3>
                <p class="empty-lead">{{ t('deploySettings.hooks.empty') }}</p>
              </template>
              <el-button type="primary" size="default" @click="addHook">
                + {{ t('deploySettings.hooks.addHook') }}
              </el-button>
            </el-empty>
          </div>

          <div v-else>
            <div class="hosts-toolbar">
              <span class="hosts-count muted">
                {{ t('deploySettings.hooks.count', { n: settings.hooks.length }) }}
              </span>
              <el-button type="primary" size="default" @click="addHook">
                + {{ t('deploySettings.hooks.addHook') }}
              </el-button>
            </div>

            <div class="hooks-grid">
              <el-card
                v-for="(hook, idx) in settings.hooks"
                :key="idx"
                class="hook-card"
                :class="{ 'hook-disabled': hook.enabled === false }"
                shadow="hover"
              >
                <template #header>
                  <div class="hook-card-header">
                    <div class="hook-card-meta">
                      <el-tag :type="eventTagType(hook.event)" size="small" effect="dark">
                        {{ hook.event }}
                      </el-tag>
                      <el-tag size="small" effect="plain">{{ hook.type }}</el-tag>
                      <span class="hook-desc-text">
                        {{ hook.description || t('deploySettings.hooks.untitled') }}
                      </span>
                    </div>
                    <div class="hook-card-actions">
                      <el-switch
                        :model-value="hook.enabled !== false"
                        size="small"
                        :aria-label="t('deploySettings.hooks.enabledAria', { n: idx + 1 })"
                        @update:model-value="(v: any) => { hook.enabled = !!v }"
                      />
                      <el-button
                        size="small" text
                        :disabled="idx === 0"
                        :aria-label="t('deploySettings.hooks.moveUp', { n: idx + 1 })"
                        @click="moveHook(idx, -1)"
                      >&#8593;</el-button>
                      <el-button
                        size="small" text
                        :disabled="idx === settings.hooks.length - 1"
                        :aria-label="t('deploySettings.hooks.moveDown', { n: idx + 1 })"
                        @click="moveHook(idx, 1)"
                      >&#8595;</el-button>
                      <el-button
                        size="small" text type="primary"
                        :loading="testingHookIdx === idx"
                        :disabled="testingHookIdx !== null || !hook.command"
                        :aria-label="t('deploySettings.hooks.testAria', { n: idx + 1 })"
                        @click="onTestHook(idx, hook)"
                      >{{ t('deploySettings.hooks.test') }}</el-button>
                      <el-button
                        size="small" text type="danger"
                        :aria-label="t('deploySettings.hooks.removeAria', { n: idx + 1 })"
                        @click="removeHook(idx)"
                      >{{ t('deploySettings.hooks.remove') }}</el-button>
                    </div>
                  </div>
                </template>

                <div class="hook-card-body">
                  <el-form
                    :model="hook"
                    label-position="top"
                    size="small"
                    :aria-label="t('deploySettings.hooks.ariaForm', { n: idx + 1 })"
                  >
                    <el-form-item :label="t('deploySettings.hooks.description')">
                      <el-input
                        :id="`hook-desc-${idx}`"
                        v-model="hook.description"
                        :placeholder="t('deploySettings.hooks.descriptionPlaceholder')"
                        clearable
                      />
                    </el-form-item>

                    <div class="hook-row-flex">
                      <el-form-item :label="t('deploySettings.hooks.event')" required style="width: 170px">
                        <el-select :id="`hook-event-${idx}`" v-model="hook.event" aria-required="true">
                          <el-option label="pre_deploy" value="pre_deploy" />
                          <el-option label="post_fetch" value="post_fetch" />
                          <el-option label="pre_switch" value="pre_switch" />
                          <el-option label="post_switch" value="post_switch" />
                          <el-option label="on_failure" value="on_failure" />
                          <el-option label="on_rollback" value="on_rollback" />
                        </el-select>
                      </el-form-item>

                      <el-form-item :label="t('deploySettings.hooks.type')" required style="width: 110px">
                        <el-select :id="`hook-type-${idx}`" v-model="hook.type" aria-required="true">
                          <el-option label="shell" value="shell" />
                          <el-option label="http" value="http" />
                          <el-option label="php" value="php" />
                        </el-select>
                      </el-form-item>

                      <el-form-item :label="t('deploySettings.hooks.timeout')" style="width: 110px">
                        <el-input-number
                          :id="`hook-timeout-${idx}`"
                          v-model="hook.timeoutSeconds"
                          :min="1" :max="3600"
                          controls-position="right"
                          style="width: 100%"
                        />
                      </el-form-item>
                    </div>

                    <el-form-item required>
                      <template #label>
                        <label :for="`hook-cmd-${idx}`">
                          {{ hook.type === 'http' ? t('deploySettings.hooks.url') : t('deploySettings.hooks.command') }}
                        </label>
                      </template>
                      <el-input
                        :id="`hook-cmd-${idx}`"
                        v-model="hook.command"
                        :placeholder="hook.type === 'http'
                          ? t('deploySettings.hooks.urlPlaceholder')
                          : t('deploySettings.hooks.commandPlaceholder')"
                        type="textarea"
                        :rows="2"
                        aria-required="true"
                      />
                    </el-form-item>
                  </el-form>
                </div>
              </el-card>
            </div>
          </div>
        </div>
      </el-tab-pane>

      <!-- ── D: Notifications (redesign #156 — per-channel cards) ─── -->
      <el-tab-pane name="notifications" :label="t('deploySettings.tabs.notifications')">
        <div class="section-body">
          <div class="section-intro">
            <h2 class="section-h2">{{ t('deploySettings.notifications.title') }}</h2>
            <p class="section-lead">{{ t('deploySettings.notifications.lead') }}</p>
          </div>

          <div class="notify-grid">
            <!-- Slack channel card -->
            <el-card class="notify-card" shadow="hover">
              <template #header>
                <div class="notify-card-header">
                  <div class="notify-card-title">
                    <span class="notify-channel-icon" aria-hidden="true">#</span>
                    <span>{{ t('deploySettings.notifications.slackTitle') }}</span>
                  </div>
                  <el-tag
                    :type="settings.notifications.slackWebhook ? 'success' : 'info'"
                    size="small" effect="plain"
                  >
                    {{ settings.notifications.slackWebhook
                        ? t('deploySettings.notifications.statusOn')
                        : t('deploySettings.notifications.statusOff') }}
                  </el-tag>
                </div>
              </template>
              <p class="notify-card-lead muted">{{ t('deploySettings.notifications.slackLead') }}</p>
              <el-form-item :label="t('deploySettings.notifications.slackWebhook')">
                <el-input
                  :id="ids.slackWebhook"
                  v-model="settings.notifications.slackWebhook"
                  :placeholder="t('deploySettings.notifications.slackWebhookPlaceholder')"
                  clearable
                />
              </el-form-item>
              <el-button
                size="small" plain
                :loading="testingSlack"
                :disabled="testingSlack || !settings.notifications.slackWebhook"
                @click="onTestSlack"
              >
                {{ t('deploySettings.notifications.testSlack') }}
              </el-button>
            </el-card>

            <!-- Email channel card -->
            <el-card class="notify-card" shadow="hover">
              <template #header>
                <div class="notify-card-header">
                  <div class="notify-card-title">
                    <span class="notify-channel-icon" aria-hidden="true">@</span>
                    <span>{{ t('deploySettings.notifications.emailTitle') }}</span>
                  </div>
                  <el-tag
                    :type="settings.notifications.emailRecipients.length > 0 ? 'success' : 'info'"
                    size="small" effect="plain"
                  >
                    {{ settings.notifications.emailRecipients.length > 0
                        ? t('deploySettings.notifications.statusCount',
                            { n: settings.notifications.emailRecipients.length })
                        : t('deploySettings.notifications.statusOff') }}
                  </el-tag>
                </div>
              </template>
              <p class="notify-card-lead muted">{{ t('deploySettings.notifications.emailLead') }}</p>
              <el-form-item :label="t('deploySettings.notifications.emailRecipients')">
                <div class="chip-input-wrap">
                  <el-tag
                    v-for="(email, i) in settings.notifications.emailRecipients"
                    :key="i"
                    closable
                    class="email-chip"
                    :aria-label="t('deploySettings.notifications.emailAriaChip', { email })"
                    @close="removeEmailRecipient(i)"
                  >
                    {{ email }}
                  </el-tag>
                  <el-input
                    :id="ids.emailInput"
                    v-model="emailInputValue"
                    size="small"
                    :placeholder="t('deploySettings.notifications.emailPlaceholder')"
                    class="chip-input"
                    :aria-label="t('deploySettings.notifications.emailAriaAdd')"
                    @keydown.enter.prevent="addEmailRecipient"
                    @keydown.tab.prevent="addEmailRecipient"
                    @blur="addEmailRecipient"
                  />
                </div>
              </el-form-item>
            </el-card>

            <!-- Triggers card — applies across both channels -->
            <el-card class="notify-card notify-card-wide" shadow="hover">
              <template #header>
                <div class="notify-card-header">
                  <div class="notify-card-title">
                    <span class="notify-channel-icon" aria-hidden="true">★</span>
                    <span>{{ t('deploySettings.notifications.triggersTitle') }}</span>
                  </div>
                  <el-tag size="small" effect="plain">
                    {{ t('deploySettings.notifications.triggersCount',
                        { n: settings.notifications.notifyOn.length }) }}
                  </el-tag>
                </div>
              </template>
              <p class="notify-card-lead muted">{{ t('deploySettings.notifications.triggersLead') }}</p>
              <el-checkbox-group
                v-model="settings.notifications.notifyOn"
                aria-labelledby="notify-on-label"
                class="notify-triggers-group"
              >
                <el-checkbox value="success">{{ t('deploySettings.notifications.stateSuccess') }}</el-checkbox>
                <el-checkbox value="failure">{{ t('deploySettings.notifications.stateFailure') }}</el-checkbox>
                <el-checkbox value="awaiting_soak">{{ t('deploySettings.notifications.stateAwaitingSoak') }}</el-checkbox>
                <el-checkbox value="cancelled">{{ t('deploySettings.notifications.stateCancelled') }}</el-checkbox>
              </el-checkbox-group>
            </el-card>
          </div>
        </div>
      </el-tab-pane>

      <!-- ── E: Advanced (redesign #171 — grouped into Lifecycle + Env cards) ── -->
      <el-tab-pane name="advanced" :label="t('deploySettings.tabs.advanced')">
        <div class="section-body">
          <div class="section-intro">
            <h2 class="section-h2">{{ t('deploySettings.advanced.title') }}</h2>
            <p class="section-lead">{{ t('deploySettings.advanced.lead') }}</p>
          </div>

          <div class="advanced-grid">
            <!-- Lifecycle card — release retention, lock timeout, concurrency -->
            <el-card class="adv-card" shadow="hover">
              <template #header>
                <div class="adv-card-header">
                  <span class="adv-card-title">
                    <span class="adv-icon" aria-hidden="true">↻</span>
                    {{ t('deploySettings.advanced.lifecycleTitle') }}
                  </span>
                </div>
              </template>
              <p class="adv-card-lead muted">{{ t('deploySettings.advanced.lifecycleLead') }}</p>

              <el-form :model="settings.advanced" label-position="top" size="default">
                <el-form-item required>
                  <template #label>
                    <label :for="ids.keepReleases">{{ t('deploySettings.advanced.keepReleases') }}</label>
                  </template>
                  <el-input-number
                    :id="ids.keepReleases"
                    v-model="settings.advanced.keepReleases"
                    :min="1" :max="50"
                    controls-position="right"
                    aria-required="true"
                    style="width: 140px"
                  />
                  <div class="field-hint">{{ t('deploySettings.advanced.keepReleasesHint') }}</div>
                </el-form-item>

                <el-form-item required>
                  <template #label>
                    <label :for="ids.lockTimeout">{{ t('deploySettings.advanced.lockTimeout') }}</label>
                  </template>
                  <el-input-number
                    :id="ids.lockTimeout"
                    v-model="settings.advanced.lockTimeoutSeconds"
                    :min="30" :max="3600"
                    controls-position="right"
                    aria-required="true"
                    style="width: 140px"
                  />
                  <div class="field-hint">{{ t('deploySettings.advanced.lockTimeoutHint') }}</div>
                </el-form-item>

                <el-form-item>
                  <template #label>
                    <label :for="ids.allowConcurrent">{{ t('deploySettings.advanced.allowConcurrent') }}</label>
                  </template>
                  <el-switch
                    :id="ids.allowConcurrent"
                    v-model="settings.advanced.allowConcurrentHosts"
                    :active-text="t('deploySettings.advanced.switchAllowed')"
                    :inactive-text="t('deploySettings.advanced.switchSerialised')"
                  />
                </el-form-item>
              </el-form>
            </el-card>

            <!-- Environment vars card — key/value list + add row -->
            <el-card class="adv-card" shadow="hover">
              <template #header>
                <div class="adv-card-header">
                  <span class="adv-card-title">
                    <span class="adv-icon" aria-hidden="true">$</span>
                    {{ t('deploySettings.advanced.envVars') }}
                  </span>
                  <el-tag size="small" effect="plain">
                    {{ Object.keys(settings.advanced.envVars).length }}
                  </el-tag>
                </div>
              </template>
              <p class="adv-card-lead muted">{{ t('deploySettings.advanced.envLead') }}</p>

              <div class="env-vars-wrap">
                <div v-if="Object.keys(settings.advanced.envVars).length === 0" class="muted env-empty">
                  {{ t('deploySettings.advanced.envEmpty') }}
                </div>
                <div
                  v-for="(_, key) in settings.advanced.envVars"
                  :key="key"
                  class="env-var-row"
                >
                  <el-input
                    :value="key"
                    :placeholder="t('deploySettings.advanced.envKey')"
                    class="env-key"
                    :aria-label="t('deploySettings.advanced.envKeyAria', { key })"
                    readonly
                  />
                  <span class="env-sep">=</span>
                  <el-input
                    v-model="settings.advanced.envVars[key]"
                    :placeholder="t('deploySettings.advanced.envValue')"
                    class="env-val"
                    :aria-label="t('deploySettings.advanced.envValAria', { key })"
                  />
                  <el-button
                    size="small" text type="danger"
                    :aria-label="t('deploySettings.advanced.envRemoveAria', { key })"
                    @click="removeEnvVar(key)"
                  >
                    {{ t('deploySettings.advanced.envRemove') }}
                  </el-button>
                </div>

                <div class="env-var-row env-add-row">
                  <el-input
                    :id="ids.envKey"
                    v-model="newEnvKey"
                    :placeholder="t('deploySettings.advanced.envKey')"
                    class="env-key"
                    :aria-label="t('deploySettings.advanced.envKeyAriaNew')"
                    @keydown.enter.prevent="addEnvVar"
                  />
                  <span class="env-sep">=</span>
                  <el-input
                    :id="ids.envVal"
                    v-model="newEnvVal"
                    :placeholder="t('deploySettings.advanced.envValue')"
                    class="env-val"
                    :aria-label="t('deploySettings.advanced.envValAriaNew')"
                    @keydown.enter.prevent="addEnvVar"
                  />
                  <el-button
                    size="small" type="primary"
                    :disabled="!newEnvKey.trim()"
                    @click="addEnvVar"
                  >
                    + {{ t('deploySettings.advanced.envAdd') }}
                  </el-button>
                </div>
              </div>
            </el-card>
          </div>
        </div>
      </el-tab-pane>

    </el-tabs>

    <!-- ── Add / Edit Host Modal ──────────────────────────────── -->
    <el-dialog
      v-model="hostModalOpen"
      :title="editingHost ? t('deploySettings.hostDialog.editTitle') : t('deploySettings.hostDialog.addTitle')"
      width="580px"
      :close-on-click-modal="false"
      destroy-on-close
      @open="onHostModalOpen"
    >
      <el-form
        ref="hostFormRef"
        :model="hostForm"
        :rules="hostRules"
        label-position="top"
        size="default"
        :aria-label="t('deploySettings.hostDialog.addTitle')"
      >
        <el-form-item :label="t('deploySettings.hostDialog.name')" prop="name" required>
          <el-input
            id="host-name"
            v-model="hostForm.name"
            :placeholder="t('deploySettings.hostDialog.namePlaceholder')"
            :disabled="!!editingHost"
            aria-required="true"
            aria-describedby="host-name-help"
          />
          <div id="host-name-help" class="field-hint">
            {{ t('deploySettings.hostDialog.nameHelp') }}
          </div>
        </el-form-item>

        <div class="form-row">
          <el-form-item :label="t('deploySettings.hostDialog.sshHost')" prop="sshHost" required style="flex: 1">
            <el-input
              id="host-ssh-host"
              v-model="hostForm.sshHost"
              :placeholder="t('deploySettings.hostDialog.sshHostPlaceholder')"
              aria-required="true"
            />
          </el-form-item>
          <el-form-item :label="t('deploySettings.hostDialog.sshUser')" prop="sshUser" required style="width: 140px">
            <el-input
              id="host-ssh-user"
              v-model="hostForm.sshUser"
              :placeholder="t('deploySettings.hostDialog.sshUserPlaceholder')"
              aria-required="true"
            />
          </el-form-item>
          <el-form-item :label="t('deploySettings.hostDialog.port')" prop="sshPort" required style="width: 90px">
            <el-input-number
              id="host-ssh-port"
              v-model="hostForm.sshPort"
              :min="1"
              :max="65535"
              controls-position="right"
              aria-required="true"
              style="width: 90px"
            />
          </el-form-item>
        </div>

        <el-form-item :label="t('deploySettings.hostDialog.remotePath')" prop="remotePath" required>
          <el-input
            id="host-remote-path"
            v-model="hostForm.remotePath"
            :placeholder="t('deploySettings.hostDialog.remotePathPlaceholder')"
            aria-required="true"
          />
        </el-form-item>

        <el-form-item :label="t('deploySettings.hostDialog.branch')" prop="branch" required>
          <el-input
            id="host-branch"
            v-model="hostForm.branch"
            :placeholder="t('deploySettings.hostDialog.branchPlaceholder')"
            aria-required="true"
          />
        </el-form-item>

        <div class="form-row">
          <el-form-item :label="t('deploySettings.hostDialog.composerInstall')" style="flex: 1">
            <el-switch id="host-composer" v-model="hostForm.composerInstall" />
          </el-form-item>
          <el-form-item :label="t('deploySettings.hostDialog.runMigrations')" style="flex: 1">
            <el-switch id="host-migrations" v-model="hostForm.runMigrations" />
          </el-form-item>
          <el-form-item :label="t('deploySettings.hostDialog.soakSeconds')" style="width: 140px">
            <el-input-number
              id="host-soak"
              v-model="hostForm.soakSeconds"
              :min="0"
              :max="3600"
              controls-position="right"
              style="width: 120px"
            />
          </el-form-item>
        </div>

        <el-form-item :label="t('deploySettings.hostDialog.phpPath')">
          <el-input
            id="host-php"
            v-model="hostForm.phpBinaryPath"
            :placeholder="t('deploySettings.hostDialog.phpPathPlaceholder')"
            clearable
          />
        </el-form-item>

        <el-form-item :label="t('deploySettings.hostDialog.healthUrl')">
          <el-input
            id="host-health"
            v-model="hostForm.healthCheckUrl"
            :placeholder="t('deploySettings.hostDialog.healthUrlPlaceholder')"
            clearable
          />
        </el-form-item>

        <!-- Phase 7.5+++ — local-loopback paths. When set, the GUI
             deploy button can dispatch without supplying localPaths
             in the body — the daemon falls back to these. -->
        <el-divider content-position="left">
          {{ t('deploySettings.hostDialog.localPathsTitle') }}
        </el-divider>
        <div class="field-hint" style="margin-top: -8px; margin-bottom: 8px">
          {{ t('deploySettings.hostDialog.localPathsHelp') }}
        </div>
        <el-form-item :label="t('deploySettings.hostDialog.localSourcePath')">
          <el-input
            id="host-local-source"
            v-model="hostForm.localSourcePath"
            :placeholder="t('deploySettings.hostDialog.localSourcePathPlaceholder')"
            clearable
          />
        </el-form-item>
        <el-form-item :label="t('deploySettings.hostDialog.localTargetPath')">
          <el-input
            id="host-local-target"
            v-model="hostForm.localTargetPath"
            :placeholder="t('deploySettings.hostDialog.localTargetPathPlaceholder')"
            clearable
          />
        </el-form-item>

        <!-- Phase 7.5+++ nksdeploy compat — shared dirs/files. Stored
             as comma-separated text in the form, parsed/joined on
             submit/load so the v-model stays a simple string. -->
        <el-form-item :label="t('deploySettings.hostDialog.sharedDirs')">
          <el-input
            id="host-shared-dirs"
            v-model="hostForm._sharedDirsCsv"
            :placeholder="t('deploySettings.hostDialog.sharedDirsPlaceholder')"
            clearable
          />
          <div class="field-hint">{{ t('deploySettings.hostDialog.sharedDirsHelp') }}</div>
        </el-form-item>
        <el-form-item :label="t('deploySettings.hostDialog.sharedFiles')">
          <el-input
            id="host-shared-files"
            v-model="hostForm._sharedFilesCsv"
            :placeholder="t('deploySettings.hostDialog.sharedFilesPlaceholder')"
            clearable
          />
          <div class="field-hint">{{ t('deploySettings.hostDialog.sharedFilesHelp') }}</div>
        </el-form-item>
      </el-form>

      <!-- Phase 7.5+++ — TCP probe button. Surfaces inline status next
           to the action so operators see the result without leaving
           the dialog. Disabled while no host is set or while in flight. -->
      <div v-if="hostForm.sshHost" class="test-conn-row">
        <el-button
          size="small" plain
          :loading="testingConnection"
          :disabled="!hostForm.sshHost || !hostForm.sshPort"
          @click="onTestConnection"
        >
          {{ t('deploySettings.hostDialog.testConnection') }}
        </el-button>
        <span v-if="testConnectionResult" class="test-conn-result"
              :class="{ ok: testConnectionResult.ok, fail: !testConnectionResult.ok }">
          <el-icon v-if="testConnectionResult.ok"><CircleCheck /></el-icon>
          <el-icon v-else><CircleClose /></el-icon>
          <span v-if="testConnectionResult.ok">
            {{ t('deploySettings.hostDialog.testConnectionOk',
                 { ms: testConnectionResult.latencyMs }) }}
          </span>
          <span v-else>{{ testConnectionResult.error }}</span>
        </span>
      </div>

      <template #footer>
        <el-button @click="hostModalOpen = false">{{ t('deploySettings.common.cancel') }}</el-button>
        <el-button type="primary" @click="submitHostForm">
          {{ editingHost ? t('deploySettings.common.edit') : t('deploySettings.hosts.addHost') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, computed, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, ElNotification } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { useDeploySettingsStore } from '../../stores/deploySettings'
import { CircleCheck, CircleClose } from '@element-plus/icons-vue'
import HostSettingsCard from './HostSettingsCard.vue'
import { listMcpGrants, subscribeEventsMap } from '../../api/daemon'
import { onBeforeUnmount } from 'vue'
import {
  fetchDeploySnapshots,
  defaultDeploySettings,
  createDeployIntent,
  confirmDeployIntent,
  restoreSnapshot,
  snapshotNow,
  testHostConnection,
  testHook,
  testNotification,
} from '../../api/deploy'
import type { DeployHostConfig, DeployHookConfig, DeploySnapshotEntry, DeploySettings, TestHostConnectionResult } from '../../api/deploy'

const { t } = useI18n()
const props = defineProps<{ domain: string }>()

const store = useDeploySettingsStore()
const saving = ref(false)
const activeSection = ref('hosts')

// Phase 7.5+++ Settings redesign — dirty tracking by snapshotting the
// last-saved settings JSON on load + every save and comparing against
// the live reactive copy. Cheap O(n) JSON diff is plenty since the
// settings payload is small (~kilobytes).
const lastSavedSnapshot = ref<string>('')
const dirty = computed(() => JSON.stringify(settings) !== lastSavedSnapshot.value)

const SECTION_LABEL_KEYS: Record<string, string> = {
  hosts: 'deploySettings.tabs.hosts',
  snapshots: 'deploySettings.tabs.snapshots',
  hooks: 'deploySettings.tabs.hooks',
  notifications: 'deploySettings.tabs.notifications',
  advanced: 'deploySettings.tabs.advanced',
}
const activeSectionLabel = computed(() =>
  t(SECTION_LABEL_KEYS[activeSection.value] ?? 'deploySettings.tabs.hosts'))

async function discardChanges(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('deploySettings.discardConfirmMessage'),
      t('deploySettings.discardConfirmTitle'),
      { type: 'warning' },
    )
  } catch { return }
  const snap = JSON.parse(lastSavedSnapshot.value || '{}') as DeploySettings
  Object.assign(settings, snap)
  ElMessage.success(t('deploySettings.discarded'))
}

// Deep-reactive local copy that we mutate freely; saved explicitly.
const settings = reactive<DeploySettings>(defaultDeploySettings())
const snapshots = ref<DeploySnapshotEntry[]>([])

// Phase 7.5+++ — disk impact aggregates over the loaded snapshot list.
// Drives the inline "12 snapshots · 240 MB" summary; cheap reduce so
// the operator sees impact without opening a new endpoint.
// Phase 7.5+++ Snapshots timeline — derive 'kind' from the snapshot
// path. Daemon writes manual snapshots under …/manual/ and pre-deploy
// under …/pre-deploy/, so a substring check is enough to bucket them.
function snapshotKind(path: string): 'pre-deploy' | 'manual' {
  return path.includes('/pre-deploy/') ? 'pre-deploy' : 'manual'
}

// Phase 7.5+++ Hooks redesign — color-code event tags by lifecycle
// position so an operator can scan a long hook list and see which
// stage each one fires at without reading the label.
function eventTagType(event: string): 'success' | 'warning' | 'danger' | 'info' | 'primary' {
  if (event === 'pre_deploy' || event === 'post_fetch') return 'info'
  if (event === 'pre_switch' || event === 'post_switch') return 'primary'
  if (event === 'on_failure' || event === 'on_rollback') return 'danger'
  return 'info'
}
const snapshotsFilter = ref<'all' | 'pre-deploy' | 'manual'>('all')
const filteredSnapshots = computed(() =>
  snapshotsFilter.value === 'all'
    ? snapshots.value
    : snapshots.value.filter(s => snapshotKind(s.path) === snapshotsFilter.value))

const snapshotsTotalBytes = computed<number>(() =>
  snapshots.value.reduce((sum, s) => sum + (s.sizeBytes ?? 0), 0))
const snapshotsOldestAt = computed<string | null>(() => {
  if (snapshots.value.length === 0) return null
  // Endpoint returns newest-first per current Program.cs ORDER BY, so
  // the LAST element is the oldest. Falls back to min() if order ever
  // changes — defensive against contract drift.
  let oldest = snapshots.value[snapshots.value.length - 1].createdAt
  for (const s of snapshots.value) {
    if (s.createdAt && s.createdAt < oldest) oldest = s.createdAt
  }
  return oldest
})
// In-flight restore deployId — disables the OTHER snapshots' Restore
// buttons while one is running so the user can't double-fire the
// destructive flow against a different row mid-restore.
const restoringId = ref<string | null>(null)
// In-flight on-demand snapshot — locks the button + the Restore actions
// so a snapshot-now and a restore can't race for the same site's DB.
const snapshotting = ref(false)

// Stable IDs for label/input associations (WCAG 1.3.1)
const ids = {
  snapshotEnabled: 'snap-enabled',
  retentionDays: 'snap-retention',
  slackWebhook: 'notif-slack',
  emailInput: 'notif-email-input',
  keepReleases: 'adv-keep',
  lockTimeout: 'adv-lock-timeout',
  allowConcurrent: 'adv-concurrent',
  envKey: 'adv-env-key',
  envVal: 'adv-env-val',
}

// Phase 7.5+++ — MCP grants visibility per site. Counts active grants
// where targetPattern matches this domain (exact or wildcard '*'). Best-
// effort: silently degrades to 0 if MCP is disabled or the request fails.
const router = useRouter()
const mcpGrantsCount = ref<number>(0)

async function loadMcpGrantsCount(): Promise<void> {
  try {
    const result = await listMcpGrants(false, 1, 500)
    const dom = props.domain.toLowerCase()
    mcpGrantsCount.value = result.entries.filter((g) => {
      const tp = (g.targetPattern || '').toLowerCase()
      return tp === '*' || tp === dom
    }).length
  } catch {
    mcpGrantsCount.value = 0
  }
}

function goToMcpGrants(): void {
  router.push({ path: '/mcp/grants', query: { target: props.domain } })
}

// Phase 7.5+++ — also live-refresh the badge when grants change
// elsewhere (banner approve, McpGrants page bulk-revoke, plugin
// install). Cleaned up on unmount so EventSource count stays bounded.
let unsubscribeGrantSse: (() => void) | null = null

onMounted(async () => {
  const loaded = await store.loadForDomain(props.domain)
  Object.assign(settings, loaded)
  lastSavedSnapshot.value = JSON.stringify(settings)

  snapshots.value = await fetchDeploySnapshots(props.domain)
  void loadMcpGrantsCount()
  unsubscribeGrantSse = subscribeEventsMap({
    'mcp:grant-changed': () => { void loadMcpGrantsCount() },
  })
})

onBeforeUnmount(() => {
  if (unsubscribeGrantSse) { unsubscribeGrantSse(); unsubscribeGrantSse = null }
})

async function saveSettings(): Promise<void> {
  saving.value = true
  try {
    await store.save(props.domain, { ...settings })
    lastSavedSnapshot.value = JSON.stringify(settings)
    ElMessage.success(t('deploySettings.saveSuccess'))
  } catch (e) {
    const msg = e instanceof Error ? e.message : ''
    if (msg === 'PENDING_BACKEND') {
      lastSavedSnapshot.value = JSON.stringify(settings)
      ElMessage.info(t('deploySettings.saveLocalOnly'))
    } else {
      ElMessage.error(msg || t('deploySettings.saveFailed'))
    }
  } finally {
    saving.value = false
  }
}

// ── Hosts ─────────────────────────────────────────────────────────────────────

const hostModalOpen = ref(false)
const editingHost = ref<string | null>(null)
const hostFormRef = ref<FormInstance>()

// Phase 7.5+++ — TCP probe button state. Result is null until the
// operator clicks Test, then sticks until they change a field that
// invalidates it (handled via watch below) or close the dialog.
const testingConnection = ref(false)

// Phase 7.5+++ — Slack test-notification button busy state.
const testingSlack = ref(false)
async function onTestSlack(): Promise<void> {
  if (testingSlack.value) return
  testingSlack.value = true
  try {
    const r = await testNotification(props.domain, {
      slackWebhook: settings.notifications.slackWebhook,
    })
    if (r.ok) {
      ElNotification({
        title: t('deploySettings.notifications.testSlackOkTitle'),
        message: t('deploySettings.notifications.testSlackOkBody', { ms: r.durationMs }),
        type: 'success',
        duration: 4000,
      })
    } else {
      ElNotification({
        title: t('deploySettings.notifications.testSlackFailTitle'),
        message: r.error ?? t('deploySettings.notifications.testSlackFailGeneric'),
        type: 'error',
        duration: 8000,
      })
    }
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploySettings.notifications.testSlackFailGeneric'))
  } finally {
    testingSlack.value = false
  }
}

// Phase 7.5+++ — track which hook is currently being test-fired so the
// rest can be disabled (one fire at a time keeps the operator from
// stacking concurrent process launches that step on each other's
// stdout in the daemon log).
const testingHookIdx = ref<number | null>(null)
async function onTestHook(idx: number, hook: DeployHookConfig): Promise<void> {
  if (testingHookIdx.value !== null) return
  testingHookIdx.value = idx
  try {
    const r = await testHook(props.domain, {
      type: hook.type,
      command: hook.command,
      timeoutSeconds: hook.timeoutSeconds,
      description: hook.description,
    })
    if (r.ok) {
      ElNotification({
        title: t('deploySettings.hooks.testOkTitle'),
        message: t('deploySettings.hooks.testOkBody', { ms: r.durationMs }),
        type: 'success',
        duration: 4000,
      })
    } else {
      ElNotification({
        title: t('deploySettings.hooks.testFailTitle'),
        message: r.error ?? t('deploySettings.hooks.testFailGeneric'),
        type: 'error',
        duration: 8000,
      })
    }
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploySettings.hooks.testFailGeneric'))
  } finally {
    testingHookIdx.value = null
  }
}
const testConnectionResult = ref<TestHostConnectionResult | null>(null)

async function onTestConnection(): Promise<void> {
  if (testingConnection.value) return
  testingConnection.value = true
  testConnectionResult.value = null
  try {
    testConnectionResult.value = await testHostConnection(
      hostForm.sshHost.trim(),
      hostForm.sshPort,
    )
  } catch (e) {
    testConnectionResult.value = {
      ok: false,
      error: t('deploySettings.hostDialog.testConnectionRequestFailed', {
        error: (e as Error).message,
      }),
    }
  } finally {
    testingConnection.value = false
  }
}

// Form-shape extends DeployHostConfig with two CSV fields backing
// sharedDirs/sharedFiles arrays. Conversion happens on open/submit.
type HostForm = DeployHostConfig & { _sharedDirsCsv: string; _sharedFilesCsv: string }

function emptyHostForm(): HostForm {
  return {
    name: '',
    sshHost: '',
    sshUser: 'deploy',
    sshPort: 22,
    remotePath: '',
    branch: 'main',
    phpBinaryPath: '',
    composerInstall: true,
    runMigrations: true,
    soakSeconds: 30,
    healthCheckUrl: '',
    localSourcePath: '',
    localTargetPath: '',
    sharedDirs: [],
    sharedFiles: [],
    _sharedDirsCsv: '',
    _sharedFilesCsv: '',
  }
}

const hostForm = reactive<HostForm>(emptyHostForm())

const hostRules: FormRules = {
  name: [
    { required: true, message: 'Name is required', trigger: 'blur' },
    { pattern: /^[a-z0-9_-]+$/, message: 'Use lowercase letters, digits, hyphens, underscores', trigger: 'blur' },
  ],
  sshHost: [{ required: true, message: 'SSH host is required', trigger: 'blur' }],
  sshUser: [{ required: true, message: 'SSH user is required', trigger: 'blur' }],
  remotePath: [{ required: true, message: 'Remote path is required', trigger: 'blur' }],
  branch: [{ required: true, message: 'Branch is required', trigger: 'blur' }],
}

function openAddHostModal(): void {
  editingHost.value = null
  Object.assign(hostForm, emptyHostForm())
  hostModalOpen.value = true
}

function openEditHostModal(host: DeployHostConfig): void {
  editingHost.value = host.name
  Object.assign(hostForm, {
    ...host,
    _sharedDirsCsv: (host.sharedDirs ?? []).join(', '),
    _sharedFilesCsv: (host.sharedFiles ?? []).join(', '),
  })
  hostModalOpen.value = true
}

function onHostModalOpen(): void {
  hostFormRef.value?.clearValidate()
  // Reset the TCP probe result every time the dialog opens — a probe
  // result from the last add/edit should NOT carry over to the next.
  testConnectionResult.value = null
}

async function submitHostForm(): Promise<void> {
  const valid = await hostFormRef.value?.validate().catch(() => false)
  if (!valid) return

  const payload: DeployHostConfig = {
    name: hostForm.name,
    sshHost: hostForm.sshHost,
    sshUser: hostForm.sshUser,
    sshPort: hostForm.sshPort,
    remotePath: hostForm.remotePath,
    branch: hostForm.branch,
    composerInstall: hostForm.composerInstall,
    runMigrations: hostForm.runMigrations,
    soakSeconds: hostForm.soakSeconds,
  }
  if (hostForm.phpBinaryPath) payload.phpBinaryPath = hostForm.phpBinaryPath
  if (hostForm.healthCheckUrl) payload.healthCheckUrl = hostForm.healthCheckUrl
  if (hostForm.localSourcePath) payload.localSourcePath = hostForm.localSourcePath
  if (hostForm.localTargetPath) payload.localTargetPath = hostForm.localTargetPath
  const sharedDirs = hostForm._sharedDirsCsv.split(',').map(s => s.trim()).filter(Boolean)
  const sharedFiles = hostForm._sharedFilesCsv.split(',').map(s => s.trim()).filter(Boolean)
  if (sharedDirs.length > 0) payload.sharedDirs = sharedDirs
  if (sharedFiles.length > 0) payload.sharedFiles = sharedFiles

  if (editingHost.value) {
    store.updateHost(props.domain, editingHost.value, payload)
    const idx = settings.hosts.findIndex(h => h.name === editingHost.value)
    if (idx !== -1) settings.hosts.splice(idx, 1, { ...payload })
  } else {
    if (settings.hosts.some(h => h.name === payload.name)) {
      ElMessage.error(`A host named "${payload.name}" already exists`)
      return
    }
    store.addHost(props.domain, payload)
    settings.hosts.push({ ...payload })
  }
  hostModalOpen.value = false
  ElMessage.success(editingHost.value ? 'Host updated' : 'Host added')
}

async function removeHost(name: string): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `Remove host "${name}"?`,
      'Confirm removal',
      { type: 'warning', confirmButtonText: 'Remove', cancelButtonText: 'Cancel' },
    )
  } catch {
    return
  }
  store.removeHost(props.domain, name)
  const idx = settings.hosts.findIndex(h => h.name === name)
  if (idx !== -1) settings.hosts.splice(idx, 1)
  ElMessage.success(`Host "${name}" removed`)
}

// ── Hooks ─────────────────────────────────────────────────────────────────────

function defaultHook(): DeployHookConfig {
  // Phase 7.5+++ — explicitly enabled by default (matches the
  // `?? true` fallback we apply when reading legacy configs without
  // the field). Description starts empty so operators see the
  // placeholder hint rather than baked-in noise.
  return { event: 'post_switch', type: 'shell', command: '', timeoutSeconds: 30, enabled: true, description: '' }
}

function addHook(): void {
  settings.hooks.push(defaultHook())
}

function removeHook(idx: number): void {
  settings.hooks.splice(idx, 1)
}

function moveHook(idx: number, delta: -1 | 1): void {
  const target = idx + delta
  if (target < 0 || target >= settings.hooks.length) return
  const tmp = settings.hooks[idx]
  settings.hooks[idx] = settings.hooks[target]
  settings.hooks[target] = tmp
}

// ── Notifications ─────────────────────────────────────────────────────────────

const emailInputValue = ref('')

function addEmailRecipient(): void {
  const val = emailInputValue.value.trim()
  if (!val) return
  if (settings.notifications.emailRecipients.includes(val)) {
    emailInputValue.value = ''
    return
  }
  settings.notifications.emailRecipients.push(val)
  emailInputValue.value = ''
}

function removeEmailRecipient(idx: number): void {
  settings.notifications.emailRecipients.splice(idx, 1)
}

// ── Advanced / env vars ───────────────────────────────────────────────────────

const newEnvKey = ref('')
const newEnvVal = ref('')

function addEnvVar(): void {
  const key = newEnvKey.value.trim()
  if (!key) return
  settings.advanced.envVars[key] = newEnvVal.value
  newEnvKey.value = ''
  newEnvVal.value = ''
}

function removeEnvVar(key: string): void {
  delete settings.advanced.envVars[key]
  // Trigger reactivity — reassign the object
  settings.advanced.envVars = { ...settings.advanced.envVars }
}

// ── Snapshots ─────────────────────────────────────────────────────────────────

/**
 * Phase 6.6 — fire an on-demand snapshot. No type-to-confirm because
 * snapshot is non-destructive (READS the live DB; no DB writes). Refreshes
 * the snapshot list so the new entry appears immediately.
 */
async function onSnapshotNow(): Promise<void> {
  if (snapshotting.value) return
  snapshotting.value = true
  try {
    const result = await snapshotNow(props.domain)
    ElMessage.success(
      `Snapshot ok (${formatBytes(result.sizeBytes)} in ${result.durationMs} ms)`,
    )
    snapshots.value = await fetchDeploySnapshots(props.domain)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(`Snapshot failed: ${msg}`)
  } finally {
    snapshotting.value = false
  }
}

/**
 * Phase 6.4 — operator-driven snapshot restore. Confirm via type-to-match
 * dialog (mirrors the destructive-deploy modal pattern), mint+confirm an
 * intent token, POST to the daemon's restore endpoint with the X-Intent-Token
 * header. Refreshes the snapshot list afterwards in case the safety .bak
 * created by the SQLite restore mode shows up as a new entry.
 */
async function onRestoreSnapshot(snapshot: DeploySnapshotEntry): Promise<void> {
  if (restoringId.value !== null) return
  // Type-to-confirm so a stray click can't fire the destructive flow.
  // The deployId short-form is enough to cross-reference with the
  // archive path the daemon shows in error messages.
  const shortId = snapshot.id.slice(0, 8)
  try {
    await ElMessageBox.prompt(
      `Type "${shortId}" to confirm restore. This will OVERWRITE the live database for ${props.domain} ` +
        `with the snapshot taken at ${formatDate(snapshot.createdAt)}. ` +
        `SQLite restores create a .bak safety copy; SQL restores do NOT — make sure you have a separate backup.`,
      'Restore database snapshot',
      {
        confirmButtonText: 'Restore',
        confirmButtonClass: 'el-button--danger',
        cancelButtonText: 'Cancel',
        type: 'warning',
        inputPattern: new RegExp(`^${shortId}$`),
        inputErrorMessage: `Type exactly "${shortId}" to enable Restore`,
      },
    )
  } catch {
    return // user cancelled
  }

  restoringId.value = snapshot.id
  try {
    // Restore intent uses the synthetic host marker matching the daemon's
    // CheckIntentAsync — see NksDeployRoutes.PostSnapshotRestore.
    const intent = await createDeployIntent(props.domain, '*restore*', 'restore')
    await confirmDeployIntent(intent.intentId)
    const result = await restoreSnapshot(props.domain, snapshot.id, intent.intentToken)
    // Phase 7.5+++ — daemon now performs a real extract+symlink swap. Surface
    // the resulting release dir so the operator can confirm what landed.
    // Falls back to the older mode/bytesProcessed shape for legacy daemons.
    if (result.error) {
      ElMessage.error(t('deploySettings.snapshot.restoreFailed', { error: result.error }))
    } else if (result.swappedTo) {
      const releaseName = result.swappedTo.split(/[/\\]/).pop() ?? ''
      ElNotification({
        title: t('deploySettings.snapshot.restoreOkTitle'),
        message: t('deploySettings.snapshot.restoreOkBody',
          { release: releaseName, size: formatBytes(result.backupSizeBytes ?? 0) }),
        type: 'success',
        duration: 6000,
      })
    } else if (result.mode) {
      ElMessage.success(
        `Restore ok (${result.mode}, ${formatBytes(result.bytesProcessed ?? 0)} in ${result.durationMs ?? 0} ms)`,
      )
    } else {
      ElMessage.success(t('deploySettings.snapshot.restoreOkGeneric'))
    }
    // Refresh — SQLite restore creates a new .bak that may show up if
    // the daemon ever lists those, and the user wants visual feedback
    // that the restore landed. The new local-loopback restore also
    // appends a deploy_runs row that affects history queries.
    snapshots.value = await fetchDeploySnapshots(props.domain)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(`Restore failed: ${msg}`)
  } finally {
    restoringId.value = null
  }
}

// ── Formatters ────────────────────────────────────────────────────────────────

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString()
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

// Expose for parent, if needed
defineExpose({ saveSettings })
</script>

<style scoped>
.settings-panel {
  display: flex;
  flex-direction: column;
  gap: 0;
}

/* Phase 7.5+++ Settings redesign — sticky save bar styling */
.settings-header {
  position: sticky;
  top: 0;
  z-index: 10;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 16px;
  background: var(--el-bg-color);
  border-bottom: 1px solid var(--el-border-color-lighter);
  margin-bottom: 16px;
  gap: 12px;
}
.settings-header-left {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.settings-domain {
  font-weight: 700;
  font-size: 14px;
  font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
}
.settings-section-label {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.dirty-tag { margin-left: 4px; }
.mcp-grants-tag {
  margin-left: 4px;
  cursor: pointer;
  user-select: none;
}
.mcp-grants-tag:hover {
  background: var(--el-color-info-light-7);
}
.settings-header-actions {
  display: flex;
  gap: 8px;
}

.section-intro {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-bottom: 4px;
}
.section-h2 {
  margin: 0;
  font-size: 18px;
  font-weight: 700;
}
.section-lead {
  margin: 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  line-height: 1.5;
}

/* Hosts grid */
.hosts-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
  gap: 12px;
}
.hosts-count { font-size: 13px; }
.hosts-grid {
  display: grid;
  gap: 12px;
  grid-template-columns: 1fr;
}
.hosts-empty {
  padding: 32px 16px;
  background: var(--el-fill-color-lighter);
  border: 1px dashed var(--el-border-color-lighter);
  border-radius: 8px;
  text-align: center;
}
.empty-title {
  margin: 0 0 4px;
  font-size: 16px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}
.empty-lead {
  margin: 0 0 16px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  max-width: 480px;
}

/* Left-tab layout overrides */
.settings-tabs :deep(.el-tabs__header.is-left) {
  min-width: 140px;
}

.section-body {
  display: flex;
  flex-direction: column;
  gap: 20px;
  padding: 0 24px 24px;
  max-width: 820px;
}

.section-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.section-title {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
}

.section-footer {
  padding-top: 8px;
}

.settings-form {
  max-width: 600px;
}

.field-hint {
  margin-top: 4px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.hosts-table {
  width: 100%;
}

.mono {
  font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
  font-size: 12px;
}

.muted {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

/* Snapshot section */
.snapshot-notice {
  max-width: 600px;
}

/* Phase 7.5+++ — snapshots timeline */
.snapshots-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.snapshots-filter { margin-left: auto; }
.snapshots-summary { font-size: 12px; }
.snapshots-timeline {
  list-style: none;
  margin: 12px 0 0;
  padding: 0;
  position: relative;
}
.snapshots-timeline::before {
  content: '';
  position: absolute;
  left: 7px;
  top: 8px;
  bottom: 8px;
  width: 2px;
  background: var(--el-border-color-lighter);
}
.timeline-item {
  position: relative;
  padding: 8px 0 8px 24px;
}
.timeline-dot {
  position: absolute;
  left: 2px;
  top: 12px;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: var(--el-color-primary);
  box-shadow: 0 0 0 2px var(--el-bg-color);
}
.tl-pre-deploy .timeline-dot { background: var(--el-color-info); }
.tl-manual    .timeline-dot { background: var(--el-color-warning); }
.timeline-content {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.timeline-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.timeline-when { font-size: 13px; }
.timeline-path { font-size: 11px; word-break: break-all; }
.timeline-restore { margin-left: auto; }

.subsection {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.subsection-header {
  font-weight: 600;
  font-size: 13px;
}

/* Advanced redesign #171 — Lifecycle + Env cards */
.advanced-grid {
  display: grid;
  gap: 12px;
  grid-template-columns: 1fr;
}
.adv-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.adv-card-title {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
}
.adv-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 50%;
  font-weight: 700;
  font-size: 12px;
  color: var(--el-color-primary);
}
.adv-card-lead {
  margin: 0 0 12px;
  font-size: 12px;
  line-height: 1.5;
}
.env-empty {
  padding: 8px 0;
  font-size: 12px;
}

/* Notifications redesign #156 — per-channel cards */
.notify-grid {
  display: grid;
  gap: 12px;
  grid-template-columns: 1fr 1fr;
}
@media (max-width: 720px) {
  .notify-grid { grid-template-columns: 1fr; }
}
.notify-card-wide { grid-column: 1 / -1; }
.notify-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.notify-card-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
}
.notify-channel-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 50%;
  font-weight: 700;
  font-size: 12px;
  color: var(--el-color-primary);
}
.notify-card-lead {
  margin: 0 0 12px;
  font-size: 12px;
  line-height: 1.5;
}
.notify-triggers-group {
  display: flex;
  gap: 16px;
  flex-wrap: wrap;
}

/* Hooks redesign #155 — card grid */
.hooks-grid {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.hook-card.hook-disabled {
  opacity: 0.6;
  background: var(--el-fill-color-lighter);
}
.hook-card-header {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}
.hook-card-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  flex: 1;
  min-width: 0;
}
.hook-desc-text {
  font-size: 14px;
  font-weight: 600;
  color: var(--el-text-color-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.hook-card-actions {
  display: flex;
  align-items: center;
  gap: 4px;
}
.hook-card-body { padding-top: 4px; }
.hook-row-flex {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: flex-start;
}

/* Legacy hook-row block — kept for any pages still using it */
.hook-row {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 12px;
  border: 1px solid var(--el-border-color);
  border-radius: 6px;
  transition: opacity 0.15s ease;
}
/* Phase 7.5+++ — visually de-emphasize disabled hooks but keep them
   readable so operators can see WHAT they disabled. */
.hook-row.hook-disabled {
  opacity: 0.55;
  background: var(--el-fill-color-lighter);
}
.hook-row.hook-disabled .hook-form {
  filter: grayscale(0.5);
}

.hook-order-btns {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding-top: 24px;
}

.hook-form {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  flex: 1;
}

.hook-form :deep(.el-form-item) {
  margin-bottom: 0;
}

.hook-remove {
  padding-top: 28px;
}

.empty-state {
  padding: 24px 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

/* Notifications - email chips */
.chip-input-wrap {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
  min-height: 36px;
  padding: 4px 6px;
  border: 1px solid var(--el-border-color);
  border-radius: 4px;
  background: var(--el-fill-color-blank);
}

.email-chip {
  cursor: default;
}

.chip-input {
  flex: 1;
  min-width: 160px;
  border: none;
  outline: none;
}

.chip-input :deep(.el-input__wrapper) {
  box-shadow: none !important;
  background: transparent;
  padding: 0 4px;
}

/* Advanced - env vars */
.env-vars-wrap {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: 100%;
  max-width: 600px;
}

.env-var-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.env-key {
  width: 180px;
}

.env-val {
  flex: 1;
}

.env-sep {
  font-weight: 600;
  color: var(--el-text-color-secondary);
}

/* Form row helper */
.form-row {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
  align-items: flex-start;
}

.form-row :deep(.el-form-item) {
  margin-bottom: 0;
}

/* Phase 7.5+++ — snapshots disk-impact summary. */
.snapshots-summary {
  font-size: 13px;
  margin: 4px 0 8px;
}
.snapshots-oldest { margin-left: 6px; }

/* Phase 7.5+++ — TCP probe row in host edit dialog. */
.test-conn-row {
  display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
  margin-top: 8px; padding: 8px 12px;
  background: var(--el-fill-color-light); border-radius: 4px;
}
.test-conn-result {
  display: inline-flex; align-items: center; gap: 6px;
  font-size: 13px;
}
.test-conn-result.ok { color: var(--el-color-success); }
.test-conn-result.fail { color: var(--el-color-danger); }
</style>
