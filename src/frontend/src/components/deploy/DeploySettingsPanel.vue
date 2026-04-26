<template>
  <div class="settings-panel">
    <el-tabs v-model="activeSection" tab-position="left" class="settings-tabs">

      <!-- ── A: Hosts ──────────────────────────────────────────── -->
      <el-tab-pane name="hosts" label="Hosts">
        <div class="section-body">
          <div class="section-header">
            <h3 class="section-title">Deploy Hosts</h3>
            <el-button type="primary" size="small" @click="openAddHostModal">
              Add host
            </el-button>
          </div>

          <el-table
            :data="settings.hosts"
            size="default"
            aria-label="Deploy hosts"
            class="hosts-table"
            empty-text="No hosts configured yet"
          >
            <el-table-column label="Name" prop="name" min-width="120">
              <template #default="{ row }">
                <span class="mono">{{ row.name }}</span>
              </template>
            </el-table-column>
            <el-table-column label="SSH user@host" min-width="180">
              <template #default="{ row }">
                <span class="mono">{{ row.sshUser }}@{{ row.sshHost }}:{{ row.sshPort }}</span>
              </template>
            </el-table-column>
            <el-table-column label="Remote path" prop="remotePath" min-width="160">
              <template #default="{ row }">
                <span class="mono">{{ row.remotePath }}</span>
              </template>
            </el-table-column>
            <el-table-column label="Branch" prop="branch" width="100" />
            <el-table-column label="Auto-deploy" width="110" align="center">
              <template #default="{ row }">
                <el-tag
                  :type="row.composerInstall ? 'success' : 'info'"
                  size="small"
                  effect="plain"
                  :aria-label="row.composerInstall ? 'Composer install enabled' : 'Composer install disabled'"
                >
                  {{ row.composerInstall ? 'composer' : 'skip' }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="Actions" width="120" align="right">
              <template #default="{ row }">
                <el-button
                  size="small"
                  text
                  :aria-label="`Edit host ${row.name}`"
                  @click="openEditHostModal(row)"
                >
                  Edit
                </el-button>
                <el-button
                  size="small"
                  text
                  type="danger"
                  :aria-label="`Remove host ${row.name}`"
                  @click="removeHost(row.name)"
                >
                  Remove
                </el-button>
              </template>
            </el-table-column>
          </el-table>

          <div class="section-footer">
            <el-button type="primary" :loading="saving" @click="saveSettings">
              Save hosts
            </el-button>
          </div>
        </div>
      </el-tab-pane>

      <!-- ── B: Snapshots ──────────────────────────────────────── -->
      <el-tab-pane name="snapshots" label="DB Snapshot">
        <div class="section-body">
          <h3 class="section-title">Pre-deploy DB Snapshot</h3>

          <el-form
            :model="settings.snapshot"
            label-position="top"
            size="default"
            class="settings-form"
          >
            <el-form-item>
              <template #label>
                <label :for="ids.snapshotEnabled">Snapshot DB before each deploy</label>
              </template>
              <el-switch
                :id="ids.snapshotEnabled"
                v-model="settings.snapshot.enabled"
                active-text="Enabled"
                inactive-text="Disabled"
                aria-describedby="snapshot-help"
              />
              <div id="snapshot-help" class="field-hint">
                A database dump is created before the atomic symlink switch so you
                can restore if a migration fails.
              </div>
            </el-form-item>

            <el-form-item v-if="settings.snapshot.enabled">
              <template #label>
                <label :for="ids.retentionDays">Retention days</label>
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
              SQLite detection: Coming in Phase 6.3
            </template>
            MySQL / PostgreSQL support and automatic SQLite path detection will be
            surfaced here once the backend snapshot service is wired.
          </el-alert>

          <!-- Recent snapshots stub -->
          <div class="subsection">
            <div class="subsection-header">Recent snapshots</div>
            <div v-if="snapshots.length === 0" class="muted">
              No snapshots recorded yet.
            </div>
            <el-table v-else :data="snapshots" size="small" aria-label="Recent DB snapshots">
              <el-table-column label="Created" min-width="160">
                <template #default="{ row }">
                  {{ formatDate(row.createdAt) }}
                </template>
              </el-table-column>
              <el-table-column label="Size" width="100">
                <template #default="{ row }">
                  {{ formatBytes(row.sizeBytes) }}
                </template>
              </el-table-column>
              <el-table-column label="Actions" width="100" align="right">
                <template #default>
                  <el-button
                    size="small"
                    text
                    aria-label="Restore snapshot (coming in Phase 6.3)"
                    @click="onRestoreSnapshot"
                  >
                    Restore
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </div>

          <div class="section-footer">
            <el-button type="primary" :loading="saving" @click="saveSettings">
              Save snapshot settings
            </el-button>
          </div>
        </div>
      </el-tab-pane>

      <!-- ── C: Hooks ──────────────────────────────────────────── -->
      <el-tab-pane name="hooks" label="Hooks">
        <div class="section-body">
          <div class="section-header">
            <h3 class="section-title">Lifecycle Hooks</h3>
            <el-button type="primary" size="small" @click="addHook">
              Add hook
            </el-button>
          </div>
          <p class="muted">
            Hooks run at defined lifecycle events. Use up/down arrows to change
            execution order within the same event.
          </p>

          <div v-if="settings.hooks.length === 0" class="empty-state">
            No hooks configured.
          </div>

          <div
            v-for="(hook, idx) in settings.hooks"
            :key="idx"
            class="hook-row"
          >
            <div class="hook-order-btns">
              <el-button
                size="small"
                text
                :disabled="idx === 0"
                :aria-label="`Move hook ${idx + 1} up`"
                @click="moveHook(idx, -1)"
              >
                &#8593;
              </el-button>
              <el-button
                size="small"
                text
                :disabled="idx === settings.hooks.length - 1"
                :aria-label="`Move hook ${idx + 1} down`"
                @click="moveHook(idx, 1)"
              >
                &#8595;
              </el-button>
            </div>

            <el-form
              :model="hook"
              label-position="top"
              size="small"
              class="hook-form"
              :aria-label="`Hook ${idx + 1}`"
            >
              <el-form-item required>
                <template #label>
                  <label :for="`hook-event-${idx}`">Event</label>
                </template>
                <el-select
                  :id="`hook-event-${idx}`"
                  v-model="hook.event"
                  style="width: 160px"
                  aria-required="true"
                >
                  <el-option label="pre_deploy" value="pre_deploy" />
                  <el-option label="post_fetch" value="post_fetch" />
                  <el-option label="pre_switch" value="pre_switch" />
                  <el-option label="post_switch" value="post_switch" />
                  <el-option label="on_failure" value="on_failure" />
                  <el-option label="on_rollback" value="on_rollback" />
                </el-select>
              </el-form-item>

              <el-form-item required>
                <template #label>
                  <label :for="`hook-type-${idx}`">Type</label>
                </template>
                <el-select
                  :id="`hook-type-${idx}`"
                  v-model="hook.type"
                  style="width: 100px"
                  aria-required="true"
                >
                  <el-option label="shell" value="shell" />
                  <el-option label="http" value="http" />
                  <el-option label="php" value="php" />
                </el-select>
              </el-form-item>

              <el-form-item required style="flex: 1">
                <template #label>
                  <label :for="`hook-cmd-${idx}`">
                    {{ hook.type === 'http' ? 'URL' : 'Command / script' }}
                  </label>
                </template>
                <el-input
                  :id="`hook-cmd-${idx}`"
                  v-model="hook.command"
                  :placeholder="hook.type === 'http' ? 'https://...' : 'php artisan migrate'"
                  aria-required="true"
                />
              </el-form-item>

              <el-form-item>
                <template #label>
                  <label :for="`hook-timeout-${idx}`">Timeout (s)</label>
                </template>
                <el-input-number
                  :id="`hook-timeout-${idx}`"
                  v-model="hook.timeoutSeconds"
                  :min="1"
                  :max="3600"
                  controls-position="right"
                  style="width: 100px"
                />
              </el-form-item>
            </el-form>

            <el-button
              size="small"
              text
              type="danger"
              :aria-label="`Remove hook ${idx + 1}`"
              class="hook-remove"
              @click="removeHook(idx)"
            >
              Remove
            </el-button>
          </div>

          <div class="section-footer">
            <el-button type="primary" :loading="saving" @click="saveSettings">
              Save hooks
            </el-button>
          </div>
        </div>
      </el-tab-pane>

      <!-- ── D: Notifications ──────────────────────────────────── -->
      <el-tab-pane name="notifications" label="Notifications">
        <div class="section-body">
          <h3 class="section-title">Notifications</h3>

          <el-form
            :model="settings.notifications"
            label-position="top"
            size="default"
            class="settings-form"
          >
            <el-form-item>
              <template #label>
                <label :for="ids.slackWebhook">Slack webhook URL</label>
              </template>
              <el-input
                :id="ids.slackWebhook"
                v-model="settings.notifications.slackWebhook"
                placeholder="https://hooks.slack.com/services/..."
                clearable
              />
            </el-form-item>

            <el-form-item>
              <template #label>
                <span>Email recipients</span>
              </template>
              <div class="chip-input-wrap">
                <el-tag
                  v-for="(email, i) in settings.notifications.emailRecipients"
                  :key="i"
                  closable
                  class="email-chip"
                  :aria-label="`Recipient: ${email}`"
                  @close="removeEmailRecipient(i)"
                >
                  {{ email }}
                </el-tag>
                <el-input
                  :id="ids.emailInput"
                  v-model="emailInputValue"
                  size="small"
                  placeholder="Add email and press Enter"
                  class="chip-input"
                  aria-label="Add email recipient"
                  @keydown.enter.prevent="addEmailRecipient"
                  @keydown.tab.prevent="addEmailRecipient"
                  @blur="addEmailRecipient"
                />
              </div>
            </el-form-item>

            <el-form-item>
              <template #label>
                <span id="notify-on-label">Notify on</span>
              </template>
              <el-checkbox-group
                v-model="settings.notifications.notifyOn"
                aria-labelledby="notify-on-label"
              >
                <el-checkbox value="success">Success</el-checkbox>
                <el-checkbox value="failure">Failure</el-checkbox>
                <el-checkbox value="awaiting_soak">Awaiting soak</el-checkbox>
                <el-checkbox value="cancelled">Cancelled</el-checkbox>
              </el-checkbox-group>
            </el-form-item>
          </el-form>

          <div class="section-footer">
            <el-button type="primary" :loading="saving" @click="saveSettings">
              Save notifications
            </el-button>
          </div>
        </div>
      </el-tab-pane>

      <!-- ── E: Advanced ───────────────────────────────────────── -->
      <el-tab-pane name="advanced" label="Advanced">
        <div class="section-body">
          <h3 class="section-title">Advanced Settings</h3>

          <el-form
            :model="settings.advanced"
            label-position="top"
            size="default"
            class="settings-form"
          >
            <el-form-item required>
              <template #label>
                <label :for="ids.keepReleases">Keep N releases on remote</label>
              </template>
              <el-input-number
                :id="ids.keepReleases"
                v-model="settings.advanced.keepReleases"
                :min="1"
                :max="50"
                controls-position="right"
                aria-required="true"
                style="width: 140px"
              />
              <div class="field-hint">
                Older releases are pruned after each successful deploy.
              </div>
            </el-form-item>

            <el-form-item required>
              <template #label>
                <label :for="ids.lockTimeout">Lock timeout (seconds)</label>
              </template>
              <el-input-number
                :id="ids.lockTimeout"
                v-model="settings.advanced.lockTimeoutSeconds"
                :min="30"
                :max="3600"
                controls-position="right"
                aria-required="true"
                style="width: 140px"
              />
              <div class="field-hint">
                A deploy lock older than this is considered stale and auto-cleared.
              </div>
            </el-form-item>

            <el-form-item>
              <template #label>
                <label :for="ids.allowConcurrent">Allow concurrent deploys to different hosts</label>
              </template>
              <el-switch
                :id="ids.allowConcurrent"
                v-model="settings.advanced.allowConcurrentHosts"
                active-text="Allowed"
                inactive-text="Serialised"
              />
            </el-form-item>

            <el-form-item>
              <template #label>
                <span>Custom environment variables</span>
              </template>
              <div class="env-vars-wrap">
                <div
                  v-for="(_, key) in settings.advanced.envVars"
                  :key="key"
                  class="env-var-row"
                >
                  <el-input
                    :value="key"
                    placeholder="KEY"
                    class="env-key"
                    :aria-label="`Env var key: ${key}`"
                    readonly
                  />
                  <span class="env-sep">=</span>
                  <el-input
                    v-model="settings.advanced.envVars[key]"
                    placeholder="value"
                    class="env-val"
                    :aria-label="`Env var value for ${key}`"
                  />
                  <el-button
                    size="small"
                    text
                    type="danger"
                    :aria-label="`Remove env var ${key}`"
                    @click="removeEnvVar(key)"
                  >
                    Remove
                  </el-button>
                </div>

                <div class="env-var-row env-add-row">
                  <el-input
                    :id="ids.envKey"
                    v-model="newEnvKey"
                    placeholder="KEY"
                    class="env-key"
                    aria-label="New env var key"
                    @keydown.enter.prevent="addEnvVar"
                  />
                  <span class="env-sep">=</span>
                  <el-input
                    :id="ids.envVal"
                    v-model="newEnvVal"
                    placeholder="value"
                    class="env-val"
                    aria-label="New env var value"
                    @keydown.enter.prevent="addEnvVar"
                  />
                  <el-button
                    size="small"
                    :disabled="!newEnvKey.trim()"
                    @click="addEnvVar"
                  >
                    Add
                  </el-button>
                </div>
              </div>
            </el-form-item>
          </el-form>

          <div class="section-footer">
            <el-button type="primary" :loading="saving" @click="saveSettings">
              Save advanced settings
            </el-button>
          </div>
        </div>
      </el-tab-pane>

    </el-tabs>

    <!-- ── Add / Edit Host Modal ──────────────────────────────── -->
    <el-dialog
      v-model="hostModalOpen"
      :title="editingHost ? 'Edit host' : 'Add deploy host'"
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
        aria-label="Deploy host form"
      >
        <el-form-item label="Name (slug)" prop="name" required>
          <el-input
            id="host-name"
            v-model="hostForm.name"
            placeholder="production"
            :disabled="!!editingHost"
            aria-required="true"
            aria-describedby="host-name-help"
          />
          <div id="host-name-help" class="field-hint">
            Unique identifier used in CLI and deploy logs. Cannot be changed after creation.
          </div>
        </el-form-item>

        <div class="form-row">
          <el-form-item label="SSH host" prop="sshHost" required style="flex: 1">
            <el-input
              id="host-ssh-host"
              v-model="hostForm.sshHost"
              placeholder="192.168.1.10"
              aria-required="true"
            />
          </el-form-item>
          <el-form-item label="SSH user" prop="sshUser" required style="width: 140px">
            <el-input
              id="host-ssh-user"
              v-model="hostForm.sshUser"
              placeholder="deploy"
              aria-required="true"
            />
          </el-form-item>
          <el-form-item label="Port" prop="sshPort" required style="width: 90px">
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

        <el-form-item label="Remote path" prop="remotePath" required>
          <el-input
            id="host-remote-path"
            v-model="hostForm.remotePath"
            placeholder="/var/www/myapp"
            aria-required="true"
          />
        </el-form-item>

        <el-form-item label="Branch" prop="branch" required>
          <el-input
            id="host-branch"
            v-model="hostForm.branch"
            placeholder="main"
            aria-required="true"
          />
        </el-form-item>

        <div class="form-row">
          <el-form-item label="Composer install" style="flex: 1">
            <el-switch
              id="host-composer"
              v-model="hostForm.composerInstall"
              active-text="Yes"
              inactive-text="No"
            />
          </el-form-item>
          <el-form-item label="Run migrations" style="flex: 1">
            <el-switch
              id="host-migrations"
              v-model="hostForm.runMigrations"
              active-text="Yes"
              inactive-text="No"
            />
          </el-form-item>
          <el-form-item label="Soak seconds" style="width: 140px">
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

        <el-form-item label="PHP binary path (optional)">
          <el-input
            id="host-php"
            v-model="hostForm.phpBinaryPath"
            placeholder="/usr/bin/php8.3"
            clearable
          />
        </el-form-item>

        <el-form-item label="Health check URL (optional)">
          <el-input
            id="host-health"
            v-model="hostForm.healthCheckUrl"
            placeholder="https://example.com/healthz"
            clearable
          />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="hostModalOpen = false">Cancel</el-button>
        <el-button type="primary" @click="submitHostForm">
          {{ editingHost ? 'Update host' : 'Add host' }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { useDeploySettingsStore } from '../../stores/deploySettings'
import { fetchDeploySnapshots, defaultDeploySettings } from '../../api/deploy'
import type { DeployHostConfig, DeployHookConfig, DeploySnapshotEntry, DeploySettings } from '../../api/deploy'

const props = defineProps<{ domain: string }>()

const store = useDeploySettingsStore()
const saving = ref(false)
const activeSection = ref('hosts')

// Deep-reactive local copy that we mutate freely; saved explicitly.
const settings = reactive<DeploySettings>(defaultDeploySettings())
const snapshots = ref<DeploySnapshotEntry[]>([])

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

onMounted(async () => {
  const loaded = await store.loadForDomain(props.domain)
  Object.assign(settings, loaded)

  snapshots.value = await fetchDeploySnapshots(props.domain)
})

async function saveSettings(): Promise<void> {
  saving.value = true
  try {
    await store.save(props.domain, { ...settings })
    ElMessage.success('Settings saved')
  } catch (e) {
    const msg = e instanceof Error ? e.message : ''
    if (msg === 'PENDING_BACKEND') {
      ElMessage.info('Settings saved locally — persistence to deploy.neon coming in Phase 6.3')
    } else {
      ElMessage.error(msg || 'Failed to save settings')
    }
  } finally {
    saving.value = false
  }
}

// ── Hosts ─────────────────────────────────────────────────────────────────────

const hostModalOpen = ref(false)
const editingHost = ref<string | null>(null)
const hostFormRef = ref<FormInstance>()

function emptyHostForm(): DeployHostConfig {
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
  }
}

const hostForm = reactive<DeployHostConfig>(emptyHostForm())

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
  Object.assign(hostForm, { ...host })
  hostModalOpen.value = true
}

function onHostModalOpen(): void {
  hostFormRef.value?.clearValidate()
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
  return { event: 'post_switch', type: 'shell', command: '', timeoutSeconds: 30 }
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

function onRestoreSnapshot(): void {
  ElMessage.info('Snapshot restore coming in Phase 6.3')
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

.subsection {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.subsection-header {
  font-weight: 600;
  font-size: 13px;
}

/* Hooks */
.hook-row {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 12px;
  border: 1px solid var(--el-border-color);
  border-radius: 6px;
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
</style>
