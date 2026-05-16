<template>
  <div class="cf-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('mysqlPlugin.title') }}</h1>
        <span class="page-subtitle">{{ $t('mysqlPlugin.subtitle') }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="refresh" :loading="refreshing">{{ $t('common.refresh') }}</el-button>
        <el-button
          size="small"
          :type="serviceRunning ? 'danger' : 'success'"
          :loading="toggling"
          :disabled="!daemonStore.connected"
          @click="toggleService"
        >
          {{ serviceRunning ? $t('common.stop') : $t('common.run') }} MySQL
        </el-button>
      </div>
    </div>
    <div class="page-autostart-row">
      <PluginAutostartSwitch plugin-id="nks.wdc.mysql" />
    </div>

    <div class="status-strip">
      <div class="status-card" :class="{ 'status-active': serviceRunning }">
        <el-icon class="status-icon" :class="serviceRunning ? 'icon-running' : 'icon-stopped'">
          <CircleCheckFilled v-if="serviceRunning" /><CircleClose v-else />
        </el-icon>
        <div class="status-body">
          <div class="status-title">{{ serviceRunning ? $t('common.running') : $t('common.stopped') }}</div>
          <div class="status-meta">MySQL</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Connection /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('mysqlPlugin.port') }}: {{ mysqlPort }}</div>
          <div class="status-meta">{{ serviceInfo?.version || $t('mysqlPlugin.versionUnknown') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><DataLine /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('mysqlPlugin.connections') }}: —</div>
          <div class="status-meta">{{ $t('mysqlPlugin.connectionsMeta') }}</div>
        </div>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="cf-tabs">
      <!-- Overview -->
      <el-tab-pane name="overview">
        <template #label>
          <span class="tab-label"><el-icon><Monitor /></el-icon> {{ $t('mysqlPlugin.tabOverview') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.tabOverview') }}</span>
            </header>
            <div class="edit-card-body">
              <el-descriptions :column="2" border size="small">
                <el-descriptions-item :label="$t('mysqlPlugin.status')">
                  <el-tag :type="serviceRunning ? 'success' : 'info'" size="small" effect="dark">
                    {{ serviceRunning ? $t('common.running') : $t('common.stopped') }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.version')">{{ serviceInfo?.version || '—' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.port')">{{ mysqlPort }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.pid')">{{ serviceInfo?.pid ?? '—' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.dataDir')">{{ $t('mysqlPlugin.dataDirUnknown') }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.connections')">—</el-descriptions-item>
              </el-descriptions>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Databases -->
      <el-tab-pane name="databases">
        <template #label>
          <span class="tab-label"><el-icon><Grid /></el-icon> {{ $t('mysqlPlugin.tabDatabases') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.tabDatabases') }}</span>
              <span class="edit-card-hint">
                <el-button size="small" text @click="$router.push('/databases')">
                  {{ $t('mysqlPlugin.openDatabasesPage') }}
                </el-button>
              </span>
            </header>
            <div class="edit-card-body mysql-databases-panel">
              <el-alert
                v-if="databasesError"
                type="error"
                :closable="true"
                show-icon
                :title="databasesError"
                style="margin-bottom: 14px"
                @close="databasesError = ''"
              />

              <div class="database-toolbar">
                <div class="database-create-inline">
                  <el-input
                    v-model="newDatabaseName"
                    :placeholder="$t('databases.namePlaceholder')"
                    maxlength="64"
                    clearable
                    @keyup.enter="createDatabase"
                  />
                  <el-button type="primary" :icon="Plus" :loading="creatingDatabase" :disabled="!canCreateDatabase" @click="createDatabase">
                    {{ $t('databases.create') }}
                  </el-button>
                </div>
                <div class="database-toolbar-actions">
                  <el-tag v-if="databases.length" type="info" effect="plain">{{ databases.length }} {{ $t('mysqlPlugin.databasesCount') }}</el-tag>
                  <el-button size="small" :loading="databasesLoading" @click="loadDatabases">{{ $t('common.refresh') }}</el-button>
                </div>
              </div>

              <div v-if="databasesLoading && databases.length === 0" class="database-loading">
                <el-skeleton :rows="4" animated />
              </div>

              <el-empty v-else-if="databases.length === 0" :image-size="64">
                <template #description>
                  <span>{{ $t('databases.emptyTitle') }}</span>
                </template>
              </el-empty>

              <div v-else class="database-workspace">
                <aside class="database-list" :aria-label="$t('mysqlPluginDatabasesAria')">
                  <button
                    v-for="db in databases"
                    :key="db"
                    type="button"
                    class="database-list-item"
                    :class="{ active: selectedDatabase === db }"
                    @click="selectDatabase(db)"
                  >
                    <span class="database-name">{{ db }}</span>
                    <span v-if="databaseTables[db]" class="database-meta">{{ databaseTables[db].length }} {{ $t('databases.tables') }}</span>
                  </button>
                </aside>

                <section class="database-detail" aria-live="polite">
                  <template v-if="selectedDatabase">
                    <div class="database-detail-header">
                      <div>
                        <h2 class="database-detail-title">{{ selectedDatabase }}</h2>
                        <div class="database-detail-subtitle">{{ selectedDatabaseSize || $t('mysqlPlugin.databaseSizeUnknown') }}</div>
                      </div>
                      <div class="database-detail-actions">
                        <el-button size="small" :loading="tablesLoading" @click="loadDatabaseTables(selectedDatabase)">
                          {{ $t('databases.refreshTables') }}
                        </el-button>
                        <el-button size="small" :loading="exportingDatabase" @click="exportDatabase">
                          {{ $t('databases.exportSql') }}
                        </el-button>
                        <el-button size="small" type="danger" plain :loading="droppingDatabase" @click="confirmDropDatabase(selectedDatabase)">
                          {{ $t('databases.drop') }}
                        </el-button>
                      </div>
                    </div>

                    <div class="tables-section">
                      <div class="section-label">{{ $t('databases.tables') }}</div>
                      <el-table :data="currentDatabaseTables" size="small" stripe v-loading="tablesLoading" class="tables-table">
                        <el-table-column prop="name" :label="$t('databases.tableCol')" min-width="180">
                          <template #default="{ row }">
                            <span class="mono">{{ row.name || row }}</span>
                          </template>
                        </el-table-column>
                        <el-table-column prop="rows" :label="$t('databases.rowsCol')" width="110" align="right">
                          <template #default="{ row }">
                            <span class="mono">{{ row.rows ?? '—' }}</span>
                          </template>
                        </el-table-column>
                        <el-table-column prop="size" :label="$t('databases.sizeCol')" width="130" align="right">
                          <template #default="{ row }">
                            <span class="mono">{{ row.size ?? '—' }}</span>
                          </template>
                        </el-table-column>
                      </el-table>
                    </div>

                    <div class="query-section">
                      <div class="section-label">{{ $t('databases.sqlQuery') }}</div>
                      <el-input
                        v-model="sqlQuery"
                        type="textarea"
                        :rows="4"
                        :placeholder="$t('databases.sqlPlaceholder')"
                        class="query-input"
                      />
                      <div class="query-actions">
                        <el-button type="primary" size="small" :loading="queryRunning" :disabled="!sqlQuery.trim()" @click="executeQuery">
                          {{ $t('databases.execute') }}
                        </el-button>
                        <span v-if="queryTime !== null" class="query-time">{{ queryTime }}ms</span>
                      </div>
                      <pre v-if="queryResult" class="query-output">{{ queryResult }}</pre>
                    </div>
                  </template>
                  <el-empty v-else :image-size="56">
                    <template #description>
                      <span>{{ $t('databases.selectFirst') }}</span>
                    </template>
                  </el-empty>
                </section>
              </div>

              <div class="hint">{{ $t('mysqlPlugin.databasesEmbedHint') }}</div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Users -->
      <el-tab-pane name="users">
        <template #label>
          <span class="tab-label"><el-icon><User /></el-icon> {{ $t('mysqlPlugin.tabUsers') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.createUser') }}</span>
              <span class="edit-card-hint">{{ $t('mysqlPlugin.createUserHint') }}</span>
            </header>
            <div class="edit-card-body">
              <el-form label-width="150px" class="user-form-grid">
                <el-form-item :label="$t('mysqlPlugin.userName')">
                  <el-input v-model="userForm.userName" maxlength="64" />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.host')">
                  <el-select v-model="userForm.host" filterable allow-create default-first-option>
                    <el-option label="localhost" value="localhost" />
                    <el-option label="127.0.0.1" value="127.0.0.1" />
                    <el-option label="%" value="%" />
                  </el-select>
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.password')">
                  <el-input
                    v-model="userForm.password"
                    type="password"
                    show-password
                    :disabled="userForm.passwordless"
                  />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.passwordless')">
                  <el-switch v-model="userForm.passwordless" @change="userForm.password = ''" />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.databaseGrant')">
                  <el-select v-model="userForm.database" clearable filterable :loading="databasesLoading">
                    <el-option v-for="db in databases" :key="db" :label="db" :value="db" />
                  </el-select>
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.privileges')">
                  <el-select v-model="userForm.privileges" :disabled="!userForm.database">
                    <el-option :label="$t('mysqlPlugin.privilegeRead')" value="read" />
                    <el-option :label="$t('mysqlPlugin.privilegeReadWrite')" value="readWrite" />
                    <el-option :label="$t('mysqlPlugin.privilegeAdmin')" value="admin" />
                  </el-select>
                </el-form-item>
              </el-form>
              <div class="card-actions">
                <el-button
                  type="primary"
                  :icon="Plus"
                  :loading="creatingUser"
                  :disabled="!canCreateUser"
                  @click="createUser"
                >
                  {{ $t('mysqlPlugin.createUserBtn') }}
                </el-button>
                <span v-if="usersStatus" class="save-status" :class="usersStatus.kind">
                  {{ usersStatus.message }}
                </span>
              </div>
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.usersList') }}</span>
              <el-button size="small" :loading="usersLoading" @click="loadUsers">{{ $t('common.refresh') }}</el-button>
            </header>
            <div class="edit-card-body table-body">
              <el-alert
                v-if="usersError"
                type="error"
                :closable="false"
                show-icon
                :title="usersError"
                style="margin-bottom: 12px"
              />
              <el-table :data="users" size="small" stripe class="users-table" v-loading="usersLoading">
                <el-table-column prop="userName" :label="$t('mysqlPlugin.userName')" min-width="160" />
                <el-table-column prop="host" :label="$t('mysqlPlugin.host')" min-width="150" />
                <el-table-column prop="plugin" :label="$t('mysqlPluginAuthCol')" min-width="160" />
                <el-table-column :label="$t('mysqlPlugin.flags')" width="170">
                  <template #default="{ row }">
                    <el-tag v-if="row.accountLocked" type="warning" size="small">{{ $t('mysqlPlugin.locked') }}</el-tag>
                    <el-tag v-if="row.passwordExpired" type="danger" size="small">{{ $t('mysqlPlugin.expired') }}</el-tag>
                    <span v-if="!row.accountLocked && !row.passwordExpired" class="muted">—</span>
                  </template>
                </el-table-column>
                <el-table-column :label="$t('common.actions')" width="260" fixed="right">
                  <template #default="{ row }">
                    <div class="table-actions">
                      <el-button size="small" text :disabled="isRootUser(row)" @click="openPasswordDialog(row)">
                        {{ $t('mysqlPlugin.setPassword') }}
                      </el-button>
                      <el-button size="small" text :disabled="isRootUser(row)" @click="openGrantDialog(row)">
                        {{ $t('mysqlPlugin.grant') }}
                      </el-button>
                      <el-button size="small" text type="danger" :disabled="isRootUser(row)" @click="dropUser(row)">
                        {{ $t('common.delete') }}
                      </el-button>
                    </div>
                  </template>
                </el-table-column>
              </el-table>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Root Password -->
      <el-tab-pane name="password">
        <template #label>
          <span class="tab-label"><el-icon><Key /></el-icon> {{ $t('mysqlPlugin.tabPassword') }}</span>
        </template>
        <div class="tab-content">
          <!-- Change password -->
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.changePassword') }}</span>
              <span class="edit-card-hint">{{ $t('mysqlPlugin.changePasswordHint') }}</span>
            </header>
            <div class="edit-card-body">
              <el-form label-width="180px" size="default">
                <el-form-item :label="$t('mysqlPlugin.currentPassword')">
                  <el-input v-model="changePwd.current" type="password" show-password :placeholder="$t('mysqlPlugin.currentPasswordOptional')" style="max-width: 340px" />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.newPassword')">
                  <el-input v-model="changePwd.newPwd" type="password" show-password :disabled="changePwd.passwordless" style="max-width: 340px" />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.confirmPassword')">
                  <el-input v-model="changePwd.confirm" type="password" show-password :disabled="changePwd.passwordless" style="max-width: 340px" />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.passwordless')">
                  <el-switch v-model="changePwd.passwordless" @change="changePwd.newPwd = ''; changePwd.confirm = ''" />
                </el-form-item>
              </el-form>
              <div class="card-actions">
                <el-button
                  type="primary"
                  :loading="changingPwd"
                  :disabled="!canChangePassword"
                  @click="changePassword"
                >
                  {{ $t('mysqlPlugin.changePasswordBtn') }}
                </el-button>
                <span v-if="changePwdStatus" class="save-status" :class="changePwdStatus.kind">
                  {{ changePwdStatus.message }}
                </span>
              </div>
            </div>
          </section>

          <!-- Reset password (danger) -->
          <section class="edit-card danger-card">
            <header class="edit-card-header danger-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.resetPassword') }}</span>
              <el-tag type="danger" size="small" effect="dark">{{ $t('common.danger') }}</el-tag>
            </header>
            <div class="edit-card-body">
              <el-alert
                type="warning"
                :closable="false"
                show-icon
                style="margin-bottom: 16px"
              >
                <template #title>{{ $t('mysqlPlugin.resetWarning') }}</template>
                <template #default>
                  <p style="margin: 6px 0 0">{{ $t('mysqlPlugin.resetDescription') }}</p>
                </template>
              </el-alert>
              <el-form label-width="180px" size="default">
                <el-form-item :label="$t('mysqlPlugin.newRootPassword')">
                  <el-input v-model="resetPwd.newPwd" type="password" show-password :disabled="resetPwd.passwordless" style="max-width: 340px" />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.passwordless')">
                  <el-switch v-model="resetPwd.passwordless" @change="resetPwd.newPwd = ''" />
                </el-form-item>
              </el-form>
              <div class="card-actions">
                <el-button
                  type="danger"
                  :loading="resettingPwd"
                  @click="resetPassword"
                >
                  {{ $t('mysqlPlugin.resetPasswordBtn') }}
                </el-button>
                <span v-if="resetPwdStatus" class="save-status" :class="resetPwdStatus.kind">
                  {{ resetPwdStatus.message }}
                </span>
              </div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Tuning -->
      <el-tab-pane name="tuning">
        <template #label>
          <span class="tab-label"><el-icon><Setting /></el-icon> {{ $t('mysqlPlugin.tabTuning') }}</span>
        </template>
        <div class="tab-content">
          <el-alert
            type="info"
            :closable="false"
            show-icon
            :title="$t('mysqlPlugin.tuningPending')"
            style="margin-bottom: 16px"
          />
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.tuningParams') }}</span>
            </header>
            <div class="edit-card-body">
              <el-form label-width="240px" size="default">
                <el-form-item label="max_connections">
                  <el-input-number v-model="tuning.maxConnections" disabled :min="1" />
                </el-form-item>
                <el-form-item label="innodb_buffer_pool_size">
                  <el-input v-model="tuning.innodbBufferPoolSize" disabled style="width: 180px" />
                </el-form-item>
                <el-form-item label="query_cache_size">
                  <el-input v-model="tuning.queryCacheSize" disabled style="width: 180px" />
                </el-form-item>
              </el-form>
              <div class="hint">{{ $t('mysqlPlugin.tuningPendingHint') }}</div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Logs -->
      <el-tab-pane name="logs">
        <template #label>
          <span class="tab-label"><el-icon><Document /></el-icon> {{ $t('mysqlPlugin.tabLogs') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.tabLogs') }}</span>
            </header>
            <div class="edit-card-body" style="padding: 0">
              <LogViewer :service-id="'mysql'" />
            </div>
          </section>
        </div>
      </el-tab-pane>
    </el-tabs>

    <el-dialog v-model="passwordDialog.visible" :title="$t('mysqlPlugin.setPassword')" width="420px">
      <el-form label-width="140px">
        <el-form-item :label="$t('mysqlPlugin.userName')">
          <span>{{ passwordDialog.userName }}@{{ passwordDialog.host }}</span>
        </el-form-item>
        <el-form-item :label="$t('mysqlPlugin.newPassword')">
          <el-input v-model="passwordDialog.password" type="password" show-password :disabled="passwordDialog.passwordless" />
        </el-form-item>
        <el-form-item :label="$t('mysqlPlugin.passwordless')">
          <el-switch v-model="passwordDialog.passwordless" @change="passwordDialog.password = ''" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="passwordDialog.visible = false">{{ $t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="updatingUserPassword" @click="updateUserPassword">
          {{ $t('common.save') }}
        </el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="grantDialog.visible" :title="$t('mysqlPlugin.grant')" width="460px">
      <el-form label-width="140px">
        <el-form-item :label="$t('mysqlPlugin.userName')">
          <span>{{ grantDialog.userName }}@{{ grantDialog.host }}</span>
        </el-form-item>
        <el-form-item :label="$t('mysqlPlugin.databaseGrant')">
          <el-select v-model="grantDialog.database" filterable :loading="databasesLoading" style="width: 100%">
            <el-option v-for="db in databases" :key="db" :label="db" :value="db" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('mysqlPlugin.privileges')">
          <el-select v-model="grantDialog.privileges" style="width: 100%">
            <el-option :label="$t('mysqlPlugin.privilegeRead')" value="read" />
            <el-option :label="$t('mysqlPlugin.privilegeReadWrite')" value="readWrite" />
            <el-option :label="$t('mysqlPlugin.privilegeAdmin')" value="admin" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="grantDialog.visible = false">{{ $t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="grantingUser" :disabled="!grantDialog.database" @click="grantUser">
          {{ $t('mysqlPlugin.grant') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { CircleCheckFilled, CircleClose, Connection, Monitor, Grid, Key, Setting, Document, DataLine, User, Plus } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders, startService, stopService } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import LogViewer from '../shared/LogViewer.vue'
import PluginAutostartSwitch from '../shared/PluginAutostartSwitch.vue'

defineOptions({ name: 'MySqlPluginPage' })

const { t } = useI18n()
const route = useRoute()
const daemonStore = useDaemonStore()
interface MySqlUser {
  userName: string
  host: string
  plugin: string
  accountLocked: boolean
  passwordExpired: boolean
}

interface MySqlUsersResponse {
  users?: MySqlUser[]
  error?: string
}

interface DatabasesResponse {
  databases?: string[]
  error?: string
  hint?: string
  suggestedPort?: number
}

interface DatabaseTable {
  name: string
  rows?: number | string
  size?: string
}

type MySqlTab = 'overview' | 'databases' | 'users' | 'password' | 'tuning' | 'logs'

function normalizeTab(value: unknown): MySqlTab {
  const text = Array.isArray(value) ? value[0] : value
  if (text === 'root-password') return 'password'
  return ['overview', 'databases', 'users', 'password', 'tuning', 'logs'].includes(String(text))
    ? String(text) as MySqlTab
    : 'overview'
}

const activeTab = ref<MySqlTab>(normalizeTab(route.query.tab))
const refreshing = ref(false)
const toggling = ref(false)

const changePwd = reactive({ current: '', newPwd: '', confirm: '', passwordless: false })
const changingPwd = ref(false)
const changePwdStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

const resetPwd = reactive({ newPwd: '', passwordless: false })
const resettingPwd = ref(false)
const resetPwdStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

const users = ref<MySqlUser[]>([])
const usersLoading = ref(false)
const usersError = ref('')
const usersStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)
const creatingUser = ref(false)
const updatingUserPassword = ref(false)
const grantingUser = ref(false)
const userForm = reactive({
  userName: '',
  host: 'localhost',
  password: '',
  passwordless: false,
  database: '',
  privileges: 'readWrite',
})
const passwordDialog = reactive({
  visible: false,
  userName: '',
  host: '',
  password: '',
  passwordless: false,
})
const grantDialog = reactive({
  visible: false,
  userName: '',
  host: '',
  database: '',
  privileges: 'readWrite',
})
const databases = ref<string[]>([])
const databasesLoading = ref(false)
const databasesError = ref('')
const newDatabaseName = ref('')
const creatingDatabase = ref(false)
const selectedDatabase = ref('')
const databaseTables = reactive<Record<string, DatabaseTable[]>>({})
const databaseSizes = reactive<Record<string, string>>({})
const tablesLoading = ref(false)
const sqlQuery = ref('')
const queryResult = ref('')
const queryRunning = ref(false)
const queryTime = ref<number | null>(null)
const exportingDatabase = ref(false)
const droppingDatabase = ref(false)

const tuning = reactive({ maxConnections: 151, innodbBufferPoolSize: '128M', queryCacheSize: '0' })

const serviceInfo = computed(() => daemonStore.services.find(s => s.id === 'mysql'))
const serviceRunning = computed(() => serviceInfo.value?.state === 2 || serviceInfo.value?.status === 'running')
const mysqlPort = computed(() => (serviceInfo.value as { port?: number } | undefined)?.port ?? 3306)
const canChangePassword = computed(() =>
  (changePwd.passwordless || (changePwd.newPwd.length > 0 && changePwd.newPwd === changePwd.confirm))
)
const canCreateDatabase = computed(() => /^[A-Za-z0-9_]{1,64}$/.test(newDatabaseName.value.trim()))
const canCreateUser = computed(() =>
  userForm.userName.trim().length > 0 &&
  userForm.host.trim().length > 0 &&
  (userForm.passwordless || userForm.password.length > 0)
)
const currentDatabaseTables = computed(() => selectedDatabase.value ? (databaseTables[selectedDatabase.value] ?? []) : [])
const selectedDatabaseSize = computed(() => selectedDatabase.value ? (databaseSizes[selectedDatabase.value] ?? '') : '')

async function httpError(r: Response): Promise<Error> {
  const text = await r.text().catch(() => '')
  if (!text) return new Error(`HTTP ${r.status}`)
  try {
    const obj = JSON.parse(text) as Record<string, unknown>
    for (const key of ['error', 'detail', 'message', 'title']) {
      const value = obj[key]
      if (typeof value === 'string' && value) return new Error(value)
    }
  } catch { /* plain text */ }
  return new Error(text.length > 300 ? `${text.slice(0, 300)}...` : text)
}

async function refresh() {
  refreshing.value = true
  try {
    await Promise.all([loadUsers(), loadDatabases()])
  } finally {
    refreshing.value = false
  }
}

async function toggleService() {
  toggling.value = true
  try {
    if (serviceRunning.value) await stopService('mysql')
    else await startService('mysql')
  } catch (e) {
    ElMessage.error(t(serviceRunning.value ? 'mysqlPlugin.toast.stopFailed' : 'mysqlPlugin.toast.startFailed', { err: errorMessage(e) }))
  } finally {
    toggling.value = false
  }
}

async function changePassword() {
  if (changePwd.passwordless) {
    changePwd.newPwd = ''
    changePwd.confirm = ''
  }
  if (changePwd.newPwd !== changePwd.confirm) {
    ElMessage.warning(t('mysqlPlugin.toast.passwordsDontMatch'))
    return
  }
  changingPwd.value = true
  changePwdStatus.value = null
  try {
    const endpoint = changePwd.current.length > 0
      ? '/api/plugins/mysql/change-password'
      : '/api/plugins/mysql/reset-password'
    if (endpoint.endsWith('reset-password')) {
      await ElMessageBox.confirm(
        changePwd.passwordless
          ? t('mysqlPlugin.resetToEmptyConfirm')
          : t('mysqlPlugin.resetWithoutCurrentConfirm'),
        t('mysqlPlugin.resetPassword'),
        { type: 'warning', confirmButtonText: t('mysqlPlugin.resetPasswordBtn'), confirmButtonClass: 'el-button--danger' }
      )
    }
    const r = await fetch(`${daemonBaseUrl()}${endpoint}`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ currentPassword: changePwd.current, newPassword: changePwd.newPwd }),
    })
    if (!r.ok) {
      throw await httpError(r)
    }
    changePwdStatus.value = { kind: 'ok', message: t('mysqlPlugin.passwordChanged') }
    ElMessage.success(t('mysqlPlugin.passwordChanged'))
    changePwd.current = ''
    changePwd.newPwd = ''
    changePwd.confirm = ''
    changePwd.passwordless = false
    await Promise.all([loadDatabases(), loadUsers()])
  } catch (e) {
    if (e === 'cancel') return
    changePwdStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(t('mysqlPlugin.toast.changePasswordFailed', { err: errorMessage(e) }))
  } finally {
    changingPwd.value = false
  }
}

async function resetPassword() {
  if (resetPwd.passwordless) resetPwd.newPwd = ''
  try {
    await ElMessageBox.confirm(
      resetPwd.passwordless
        ? t('mysqlPlugin.toast.rootResetConfirmEmpty')
        : t('mysqlPlugin.toast.rootResetConfirm'),
      t('mysqlPlugin.toast.rootResetTitle'),
      { type: 'warning', confirmButtonText: t('mysqlPlugin.toast.rootResetBtn'), confirmButtonClass: 'el-button--danger' }
    )
  } catch { return }

  resettingPwd.value = true
  resetPwdStatus.value = null
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/mysql/reset-password`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ newPassword: resetPwd.newPwd }),
    })
    if (!r.ok) {
      const err: { error?: string } = await r.json().catch(() => ({}))
      throw new Error(err.error || `HTTP ${r.status}`)
    }
    resetPwdStatus.value = { kind: 'ok', message: t('mysqlPlugin.toast.rootResetStatus') }
    ElMessage.success(t('mysqlPlugin.toast.rootResetSuccess'))
    resetPwd.newPwd = ''
    resetPwd.passwordless = false
  } catch (e) {
    resetPwdStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(t('mysqlPlugin.toast.rootResetFailed', { err: errorMessage(e) }))
  } finally {
    resettingPwd.value = false
  }
}

async function loadUsers() {
  usersLoading.value = true
  usersError.value = ''
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/mysql/users`, { headers: authHeaders() })
    if (!r.ok) {
      users.value = []
      throw await httpError(r)
    }
    const data: MySqlUsersResponse = await r.json().catch(() => ({}))
    users.value = data.users ?? []
    usersError.value = data.error ?? ''
  } catch (e) {
    usersError.value = errorMessage(e)
  } finally {
    usersLoading.value = false
  }
}

async function loadDatabases() {
  databasesLoading.value = true
  databasesError.value = ''
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases`, { headers: authHeaders() })
    if (!r.ok) throw await httpError(r)
    const data: DatabasesResponse = await r.json().catch(() => ({}))
    if (data.error) {
      databasesError.value = data.hint ? `${data.error} ${data.hint}` : data.error
      databases.value = []
      return
    }
    databases.value = data.databases ?? []
    if (selectedDatabase.value && !databases.value.includes(selectedDatabase.value)) {
      selectedDatabase.value = ''
    }
  } finally {
    databasesLoading.value = false
  }
}

async function createDatabase() {
  const name = newDatabaseName.value.trim()
  if (!/^[A-Za-z0-9_]{1,64}$/.test(name)) {
    ElMessage.warning(t('mysqlPlugin.databaseNameInvalid'))
    return
  }
  creatingDatabase.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ name }),
    })
    if (!r.ok) throw await httpError(r)
    ElMessage.success(t('mysqlPlugin.databaseCreated', { name }))
    newDatabaseName.value = ''
    await loadDatabases()
    await selectDatabase(name)
  } catch (e) {
    ElMessage.error(t('mysqlPlugin.toast.createFailed', { err: errorMessage(e) }))
  } finally {
    creatingDatabase.value = false
  }
}

async function selectDatabase(db: string) {
  selectedDatabase.value = db
  sqlQuery.value = ''
  queryResult.value = ''
  queryTime.value = null
  await loadDatabaseTables(db)
}

async function loadDatabaseTables(db: string) {
  tablesLoading.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${encodeURIComponent(db)}/tables`, { headers: authHeaders() })
    if (r.ok) {
      const data: unknown = await r.json()
      const raw: unknown = (data as { tables?: unknown })?.tables ?? data ?? []
      const list = Array.isArray(raw) ? raw : []
      databaseTables[db] = list.map(item =>
        typeof item === 'string' ? { name: item } : item as DatabaseTable
      )
    } else {
      throw await httpError(r)
    }
  } catch (e) {
    ElMessage.error(t('mysqlPlugin.toast.loadTablesFailed', { err: errorMessage(e) }))
  }

  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${encodeURIComponent(db)}/size`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json().catch(() => ({}))
      databaseSizes[db] = data.size ?? data.totalSize ?? ''
    }
  } catch { /* size is optional */ } finally {
    tablesLoading.value = false
  }
}

async function executeQuery() {
  if (!selectedDatabase.value || !sqlQuery.value.trim()) return
  queryRunning.value = true
  queryResult.value = ''
  queryTime.value = null
  const start = Date.now()
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${encodeURIComponent(selectedDatabase.value)}/query`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ sql: sqlQuery.value }),
    })
    queryTime.value = Date.now() - start
    if (!r.ok) throw await httpError(r)
    const data = await r.json().catch(() => ({}))
    queryResult.value = JSON.stringify(data, null, 2)
  } catch (e) {
    queryTime.value = Date.now() - start
    queryResult.value = `Error: ${errorMessage(e)}`
  } finally {
    queryRunning.value = false
  }
}

async function exportDatabase() {
  if (!selectedDatabase.value) return
  exportingDatabase.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${encodeURIComponent(selectedDatabase.value)}/export`, { headers: authHeaders() })
    if (!r.ok) throw await httpError(r)
    const blob = await r.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${selectedDatabase.value}.sql`
    a.click()
    URL.revokeObjectURL(url)
  } catch (e) {
    ElMessage.error(t('mysqlPlugin.toast.exportFailed', { err: errorMessage(e) }))
  } finally {
    exportingDatabase.value = false
  }
}

async function confirmDropDatabase(db: string) {
  try {
    const result = await ElMessageBox.prompt(
      t('mysqlPlugin.dropDatabasePrompt', { name: db }),
      t('databases.drop'),
      {
        type: 'warning',
        inputPattern: new RegExp(`^${db.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`),
        inputErrorMessage: t('mysqlPlugin.dropDatabaseMismatch'),
        confirmButtonText: t('databases.drop'),
        confirmButtonClass: 'el-button--danger',
      }
    )
    if (result.value !== db) return
  } catch { return }

  droppingDatabase.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${encodeURIComponent(db)}`, {
      method: 'DELETE',
      headers: authHeaders(),
    })
    if (!r.ok) throw await httpError(r)
    ElMessage.success(t('mysqlPlugin.databaseDropped', { name: db }))
    selectedDatabase.value = ''
    await loadDatabases()
  } catch (e) {
    ElMessage.error(t('mysqlPlugin.toast.dropFailed', { err: errorMessage(e) }))
  } finally {
    droppingDatabase.value = false
  }
}

async function createUser() {
  creatingUser.value = true
  usersStatus.value = null
  try {
    const body = {
      userName: userForm.userName.trim(),
      host: userForm.host.trim(),
      password: userForm.passwordless ? '' : userForm.password,
      database: userForm.database,
      privileges: userForm.privileges,
    }
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/mysql/users`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (!r.ok) {
      const err: { error?: string } = await r.json().catch(() => ({}))
      throw new Error(err.error || `HTTP ${r.status}`)
    }
    usersStatus.value = { kind: 'ok', message: t('mysqlPlugin.toast.userCreatedStatus') }
    ElMessage.success(t('mysqlPlugin.toast.userCreated'))
    userForm.userName = ''
    userForm.password = ''
    userForm.passwordless = false
    await loadUsers()
  } catch (e) {
    usersStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(t('mysqlPlugin.toast.createUserFailed', { err: errorMessage(e) }))
  } finally {
    creatingUser.value = false
  }
}

function isRootUser(row: MySqlUser) {
  return row.userName.toLowerCase() === 'root'
}

function openPasswordDialog(row: MySqlUser) {
  passwordDialog.visible = true
  passwordDialog.userName = row.userName
  passwordDialog.host = row.host
  passwordDialog.password = ''
  passwordDialog.passwordless = false
}

async function updateUserPassword() {
  updatingUserPassword.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/mysql/users/password`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({
        userName: passwordDialog.userName,
        host: passwordDialog.host,
        password: passwordDialog.passwordless ? '' : passwordDialog.password,
      }),
    })
    if (!r.ok) {
      const err: { error?: string } = await r.json().catch(() => ({}))
      throw new Error(err.error || `HTTP ${r.status}`)
    }
    passwordDialog.visible = false
    ElMessage.success(t('mysqlPlugin.toast.userPasswordUpdated'))
    await loadUsers()
  } catch (e) {
    ElMessage.error(t('mysqlPlugin.toast.userPasswordUpdateFailed', { err: errorMessage(e) }))
  } finally {
    updatingUserPassword.value = false
  }
}

function openGrantDialog(row: MySqlUser) {
  grantDialog.visible = true
  grantDialog.userName = row.userName
  grantDialog.host = row.host
  grantDialog.database = ''
  grantDialog.privileges = 'readWrite'
  void loadDatabases()
}

async function grantUser() {
  grantingUser.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/mysql/users/grants`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({
        userName: grantDialog.userName,
        host: grantDialog.host,
        database: grantDialog.database,
        privileges: grantDialog.privileges,
      }),
    })
    if (!r.ok) {
      const err: { error?: string } = await r.json().catch(() => ({}))
      throw new Error(err.error || `HTTP ${r.status}`)
    }
    grantDialog.visible = false
    ElMessage.success(t('mysqlPlugin.toast.grantApplied'))
  } catch (e) {
    ElMessage.error(t('mysqlPlugin.toast.grantFailed', { err: errorMessage(e) }))
  } finally {
    grantingUser.value = false
  }
}

async function dropUser(row: MySqlUser) {
  try {
    await ElMessageBox.confirm(
      t('mysqlPlugin.toast.dropUserConfirmMessage', { user: row.userName, host: row.host }),
      t('mysqlPlugin.toast.dropUserConfirmTitle'),
      { type: 'warning', confirmButtonText: t('mysqlPlugin.toast.dropUserConfirmBtn'), confirmButtonClass: 'el-button--danger' }
    )
  } catch { return }

  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/mysql/users/drop`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ userName: row.userName, host: row.host }),
    })
    if (!r.ok) {
      const err: { error?: string } = await r.json().catch(() => ({}))
      throw new Error(err.error || `HTTP ${r.status}`)
    }
    ElMessage.success(t('mysqlPlugin.toast.userDeleted'))
    await loadUsers()
  } catch (e) {
    ElMessage.error(t('mysqlPlugin.toast.deleteUserFailed', { err: errorMessage(e) }))
  }
}

onMounted(() => {
  void loadUsers()
  void loadDatabases()
})

watch(() => route.query.tab, value => {
  activeTab.value = normalizeTab(value)
})
</script>

<style scoped>
.cf-page { min-height: 100%; background: transparent; padding: 0; }
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px;
  border-bottom: 1px solid var(--wdc-accent-glow);
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}
.page-autostart-row { padding: 10px 24px 0; max-width: 720px; }
.header-left { display: flex; flex-direction: column; gap: 2px; }
.page-title { font-size: 1.6rem; font-weight: 800; color: var(--wdc-text); margin: 0; letter-spacing: -0.02em; }
.page-subtitle { font-size: 0.78rem; color: var(--wdc-text-3); }
.header-actions { display: flex; gap: 8px; }
.status-strip { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; padding: 18px 24px 4px; }
.status-card { display: flex; align-items: center; gap: 12px; padding: 14px 16px; background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); }
.status-card.status-active { border-color: var(--wdc-status-running); }
.status-icon { font-size: 1.4rem; width: 30px; text-align: center; color: var(--wdc-text-3); }
.status-active .status-icon { color: var(--wdc-status-running); }
.status-body { display: flex; flex-direction: column; min-width: 0; }
.status-title { font-size: 0.92rem; font-weight: 700; color: var(--wdc-text); }
.status-meta { font-size: 0.72rem; color: var(--wdc-text-3); }
.cf-tabs { padding: 16px 24px; }
.tab-content { display: flex; flex-direction: column; gap: 16px; }
.edit-card { background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); overflow: hidden; }
.edit-card-header { padding: 14px 20px; background: var(--wdc-surface-2); border-bottom: 1px solid var(--wdc-border); display: flex; justify-content: space-between; align-items: center; }
.edit-card-title { font-size: 0.78rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em; color: var(--wdc-text); }
.edit-card-hint { font-size: 0.75rem; color: var(--wdc-text-3); }
.edit-card-body { padding: 18px 20px; }
.hint { margin-top: 6px; font-size: 0.78rem; color: var(--wdc-text-3); }
.card-actions { display: flex; gap: 8px; align-items: center; margin-top: 12px; }
.table-actions { display: flex; gap: 6px; align-items: center; justify-content: flex-end; flex-wrap: wrap; }
.table-body { padding: 0; }
.users-table { width: 100%; }
.database-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 16px; }
.database-create-inline { display: flex; align-items: center; gap: 8px; min-width: min(460px, 100%); }
.database-toolbar-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; justify-content: flex-end; }
.database-loading { padding: 18px 0; }
.database-workspace { display: grid; grid-template-columns: minmax(220px, 300px) minmax(0, 1fr); gap: 16px; align-items: start; }
.database-list { display: flex; flex-direction: column; gap: 6px; max-height: 560px; overflow: auto; padding-right: 4px; }
.database-list-item { width: 100%; display: flex; align-items: flex-start; justify-content: space-between; gap: 10px; border: 1px solid var(--wdc-border); background: var(--wdc-surface); color: var(--wdc-text); border-radius: var(--wdc-radius-sm); padding: 10px 14px; text-align: left; cursor: pointer; transition: box-shadow 0.15s, border-color 0.15s, background 0.15s; }
.database-list-item:hover { border-color: var(--wdc-accent-glow); box-shadow: var(--wdc-shadow-sm); }
.database-list-item.active { border-color: var(--wdc-accent); background: var(--wdc-accent-dim); box-shadow: 0 0 0 1px var(--wdc-accent-glow); }
.database-name { min-width: 0; overflow-wrap: anywhere; font-family: 'JetBrains Mono', monospace; font-size: 0.82rem; font-weight: 700; }
.database-meta { flex: 0 0 auto; color: var(--wdc-text-3); font-size: 0.72rem; white-space: nowrap; }
.database-detail { min-width: 0; border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius-lg); background: var(--wdc-surface); padding: 18px; box-shadow: var(--wdc-shadow-sm); }
.database-detail-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; margin-bottom: 14px; }
.database-detail-title { margin: 0; color: var(--wdc-text); font-family: 'JetBrains Mono', monospace; font-size: 1rem; overflow-wrap: anywhere; }
.database-detail-subtitle { margin-top: 4px; color: var(--wdc-text-3); font-size: 0.75rem; }
.database-detail-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; justify-content: flex-end; }
.tables-section { margin-bottom: 18px; }
.tables-table { width: 100%; }
.section-label { margin-bottom: 8px; color: var(--wdc-text-3); font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em; }
.mono { font-family: 'JetBrains Mono', monospace; font-size: 0.8rem; }
.query-section { margin-top: 14px; }
.query-input { margin-bottom: 8px; }
.query-input :deep(textarea) { font-family: 'JetBrains Mono', monospace !important; font-size: 0.82rem; }
.query-actions { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.query-time { color: var(--wdc-text-3); font-family: 'JetBrains Mono', monospace; font-size: 0.75rem; }
.query-output { max-height: 360px; overflow: auto; white-space: pre-wrap; word-break: break-word; margin: 0; padding: 14px 16px; border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); background: var(--wdc-surface-2); color: var(--wdc-text-2); font-family: 'JetBrains Mono', monospace; font-size: 0.76rem; line-height: 1.55; box-shadow: var(--wdc-shadow-sm); }
.user-form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(260px, 1fr));
  gap: 0 18px;
}
.user-form-grid :deep(.el-form-item) { margin-bottom: 14px; }
.muted { color: var(--wdc-text-3); }
.save-status { font-size: 0.82rem; font-weight: 600; }
.save-status.ok { color: var(--wdc-status-running); }
.save-status.err { color: var(--wdc-status-error); }
.danger-card { border-color: var(--el-color-danger-light-5); }
.danger-header { background: color-mix(in srgb, var(--el-color-danger) 8%, var(--wdc-surface-2)); border-bottom-color: var(--el-color-danger-light-5); }

@media (max-width: 560px) {
  .page-header { align-items: flex-start; flex-direction: column; gap: 12px; padding: 16px 14px 12px; }
  .header-actions { width: 100%; flex-wrap: wrap; }
  .page-autostart-row { padding: 10px 14px 0; max-width: none; }
  .status-strip { grid-template-columns: 1fr; padding: 14px 14px 4px; }
  .cf-tabs { padding-left: 14px; padding-right: 14px; }
  .cf-tabs :deep(.el-tabs__item) { padding: 0 4px; }
  .cf-tabs :deep(.el-tabs__nav-wrap.is-scrollable) { padding: 0; }
  .cf-tabs :deep(.el-tabs__nav-prev),
  .cf-tabs :deep(.el-tabs__nav-next) { display: none; }
  .tab-label { gap: 0; font-size: 0.74rem; }
  .tab-label :deep(.el-icon) { display: none; }
  .edit-card-header { align-items: flex-start; flex-direction: column; gap: 6px; }
  .edit-card-body { padding: 16px 14px; }
  .table-body { padding: 0; }
  .database-toolbar { align-items: stretch; flex-direction: column; }
  .database-create-inline { align-items: stretch; flex-direction: column; min-width: 0; }
  .database-create-inline .el-button { width: 100%; }
  .database-toolbar-actions { justify-content: flex-start; }
  .database-workspace { grid-template-columns: 1fr; }
  .database-list { max-height: 280px; }
  .database-detail { padding: 12px; }
  .database-detail-header { flex-direction: column; }
  .database-detail-actions { justify-content: flex-start; }
  .user-form-grid { display: block; }
  .user-form-grid :deep(.el-form-item) { display: block; }
  .user-form-grid :deep(.el-form-item__label) { display: block; width: auto !important; height: auto; justify-content: flex-start; line-height: 1.3; margin-bottom: 6px; text-align: left; }
  .user-form-grid :deep(.el-form-item__content) { margin-left: 0 !important; }
  .user-form-grid :deep(.el-input),
  .user-form-grid :deep(.el-select) { width: 100%; }
  .table-actions { justify-content: flex-start; }
}
</style>
