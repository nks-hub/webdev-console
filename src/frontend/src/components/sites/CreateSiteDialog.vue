<template>
  <el-dialog
    v-model="open"
    :title="$t('sites.create')"
    width="560px"
    align-center
    class="create-site-dialog"
    @opened="onOpened"
    @closed="reset"
  >
    <el-form
      ref="formRef"
      :model="form"
      :rules="rules"
      label-position="top"
      class="create-form"
      @submit.prevent="submit"
    >
      <p class="create-lead">{{ $t('sites.new.lead') }}</p>

      <el-form-item :label="$t('sites.domain')" prop="domain" required>
        <el-input
          ref="domainInput"
          v-model="form.domain"
          placeholder="myapp.loc"
          autocomplete="off"
          spellcheck="false"
        />
        <div class="field-note" aria-live="polite">
          <template v-if="previewUrl">{{ $t('sites.new.willRunAt') }} <code>{{ previewUrl }}</code></template>
          <template v-else>{{ $t('sites.new.domainHint') }}</template>
        </div>
      </el-form-item>

      <el-form-item :label="$t('sites.new.folder')" prop="documentRoot" required>
        <el-input
          v-model="form.documentRoot"
          :placeholder="docRootPlaceholder"
          class="path-input"
          spellcheck="false"
          @input="docRootTouched = !!form.documentRoot"
        >
          <template #append>
            <el-button :aria-label="$t('sites.pickFolder')" @click="pickFolder">
              <el-icon aria-hidden="true"><FolderOpened /></el-icon>
              <span>{{ $t('sites.new.pick') }}</span>
            </el-button>
          </template>
        </el-input>
        <div v-if="folderOwner" class="field-note note-warn">
          <el-icon aria-hidden="true"><WarningFilled /></el-icon> {{ $t('sites.new.folderUsed', { domain: folderOwner }) }}
        </div>
        <div v-else-if="folderState === 'exists'" class="field-note note-ok">
          <el-icon aria-hidden="true"><Check /></el-icon> {{ $t('sites.new.folderExists') }}
        </div>
        <div v-else-if="folderState === 'missing'" class="field-note note-warn">
          <el-icon aria-hidden="true"><WarningFilled /></el-icon> {{ $t('sites.new.folderMissing') }}
        </div>
        <div v-else class="field-note">{{ $t('sites.new.folderHint') }}</div>
      </el-form-item>

      <el-form-item :label="$t('sites.new.runtime')">
        <el-radio-group v-model="form.runtime" :aria-label="$t('sites.new.runtime')">
          <el-radio-button value="php">PHP</el-radio-button>
          <el-radio-button value="static">{{ $t('sites.runtimeStaticSite') }}</el-radio-button>
          <el-radio-button value="node">Node.js</el-radio-button>
        </el-radio-group>

        <div v-if="form.runtime === 'php'" class="runtime-extra">
          <template v-if="siteState.phpVersions === null || siteState.phpVersions.length">
            <el-select
              v-model="form.phpVersion"
              class="php-select"
              :aria-label="$t('sites.phpVersion')"
              :loading="siteState.phpVersions === null"
            >
              <el-option
                v-for="v in siteState.phpVersions ?? []"
                :key="v.value"
                :label="phpOptionLabel(v)"
                :value="v.value"
              />
            </el-select>
          </template>
          <div v-else class="inline-callout">
            <el-icon aria-hidden="true"><WarningFilled /></el-icon>
            <span>{{ $t('sites.new.noPhp') }}</span>
            <el-button
              size="small"
              :loading="siteState.installing.has('php')"
              @click="siteState.install('php')"
            >{{ $t('sites.new.installPhp') }}</el-button>
          </div>
        </div>

        <div v-else-if="form.runtime === 'node'" class="runtime-extra runtime-node">
          <label class="mini-label">
            {{ $t('siteEdit.upstreamPort') }}
            <el-input-number v-model="form.nodePort" :min="1024" :max="65535" controls-position="right" />
          </label>
          <label class="mini-label mini-grow">
            {{ $t('siteEdit.startCommand') }}
            <el-input v-model="form.nodeStartCommand" placeholder="npm start" clearable />
          </label>
        </div>
        <div v-if="form.runtime === 'node'" class="field-note">{{ $t('sites.new.nodeHint') }}</div>
      </el-form-item>

      <div class="toggle-item">
        <label class="toggle-line">
          <el-switch v-model="form.sslEnabled" @change="sslTouched = true" />
          <span class="toggle-label">{{ $t('sites.new.https') }}</span>
        </label>
        <div v-if="httpsNeedsSetup" class="inline-callout">
          <el-icon aria-hidden="true"><WarningFilled /></el-icon>
          <span>{{ form.sslEnabled ? $t('sites.new.httpsNeedsCa') : $t('sites.new.httpsOffNeedsCa') }}</span>
          <el-button
            size="small"
            :loading="siteState.installing.has('https')"
            @click="installHttps"
          >{{ form.sslEnabled ? $t('sites.new.installHttps') : $t('sites.new.installHttpsOn') }}</el-button>
        </div>
        <div v-else class="field-note">{{ $t('sites.new.httpsHint') }}</div>
      </div>

      <div v-if="siteState.cloudflareActive" class="toggle-item">
        <label class="toggle-line">
          <el-switch v-model="form.cloudflareTunnel" />
          <span class="toggle-label">{{ $t('sites.simple.cloudflareTunnel') }}</span>
        </label>
      </div>

      <div class="toggle-item">
        <label class="toggle-line">
          <el-switch v-model="form.createDb" :disabled="dbBlocked" />
          <span class="toggle-label">{{ $t('sites.createDatabase') }}</span>
        </label>
        <div v-if="siteState.databaseServer === 'missing'" class="inline-callout">
          <el-icon aria-hidden="true"><WarningFilled /></el-icon>
          <span>{{ $t('sites.new.mysqlMissing') }}</span>
          <el-button
            size="small"
            :loading="siteState.installing.has('mysql')"
            @click="siteState.install('mysql')"
          >{{ $t('sites.new.installMysql') }}</el-button>
        </div>
        <div v-else-if="siteState.databaseServer === 'stopped'" class="inline-callout">
          <el-icon aria-hidden="true"><WarningFilled /></el-icon>
          <span>{{ $t('sites.new.mysqlStopped') }}</span>
          <el-button size="small" @click="siteState.startDatabase()">{{ $t('sites.new.startMysql') }}</el-button>
        </div>
        <div v-else-if="siteState.databaseServer === 'starting'" class="field-note">{{ $t('sites.new.mysqlStarting') }}</div>
        <el-input
          v-else-if="form.createDb"
          v-model="form.dbName"
          class="db-name"
          :placeholder="dbNameSuggestion"
          :aria-label="$t('sites.new.dbName')"
        />
      </div>

      <details class="more-options">
        <summary>{{ $t('sites.new.more') }}</summary>

        <el-form-item :label="$t('sites.template')">
          <el-select
            v-model="form.template"
            :placeholder="$t('sites.templatePlaceholder')"
            @change="applyTemplate"
          >
            <el-option :label="$t('sites.templateNone')" value="" />
            <el-option
              v-for="tpl in templates"
              :key="tpl.id"
              :label="$t(`sites.new.template.${tpl.id}.label`)"
              :value="tpl.id"
            />
          </el-select>
          <div v-if="form.template" class="field-note">{{ $t(`sites.new.template.${form.template}.hint`) }}</div>
        </el-form-item>

        <el-form-item :label="$t('sites.aliases')">
          <el-select
            v-model="form.aliases"
            multiple
            filterable
            allow-create
            default-first-option
            :reserve-keyword="false"
            :no-data-text="$t('sites.new.aliasesEmpty')"
            placeholder="www.myapp.loc"
          />
          <div class="field-note">{{ $t('sites.new.aliasesHint') }}</div>
        </el-form-item>

        <el-form-item :label="$t('sites.bindIp')" prop="bindAddresses">
          <el-select
            v-model="form.bindAddresses"
            multiple
            clearable
            collapse-tags
            collapse-tags-tooltip
            filterable
            :loading="bindLoading"
            :disabled="bindLoading || bindOptions.length === 0"
            @change="onBindChange"
          >
            <el-option v-for="opt in bindOptions" :key="opt.value" :label="opt.label" :value="opt.value">
              <span class="bind-opt-label">{{ opt.label }}</span>
              <span v-if="opt.wildcard || opt.loopback" class="bind-opt-badge">{{ $t('sites.bindIpRecommended') }}</span>
            </el-option>
          </el-select>
          <div class="field-note">{{ $t('sites.bindIpHint') }}</div>
        </el-form-item>
      </details>

      <!-- Lets Enter submit from any text field. -->
      <button type="submit" class="submit-proxy" tabindex="-1" aria-hidden="true" />
    </el-form>

    <template #footer>
      <span v-if="creating" class="creating-note" role="status">{{ $t('sites.new.creating') }}</span>
      <el-button @click="open = false">{{ $t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="creating" @click="submit">{{ $t('sites.simple.submit') }}</el-button>
    </template>

    <FolderBrowser v-model="showBrowser" :initial-path="form.documentRoot || undefined" @select="onFolderPicked" />
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, reactive, ref, toRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { Check, FolderOpened, WarningFilled } from '@element-plus/icons-vue'
import FolderBrowser from '../shared/FolderBrowser.vue'
import siteTemplatesConfig from '../../config/site-templates.json'
import { daemonBaseUrl, fetchBindAddressOptions, fetchSystem, type BindAddressOption } from '../../api/daemon'
import type { SiteInfo } from '../../api/types'
import { useSitesStore } from '../../stores/sites'
import { useSiteApplyStore } from '../../stores/siteApply'
import { useSiteStateStore, type InstalledPhp } from '../../stores/siteState'
import { errorMessage } from '../../utils/errors'
import { pickNativeFolder } from '../../utils/pickFolder'
import { pathSeparator, siteUrl } from '../../utils/siteUrl'
import { useFolderOwner, useFolderState } from './folderCheck'

type Runtime = 'php' | 'static' | 'node'
interface SiteTemplate {
  id: string
  fields: { runtime: Runtime; docRootSuffix?: string; nodePort?: number; nodeStartCommand?: string }
}
const templates = siteTemplatesConfig.templates as SiteTemplate[]

const props = defineProps<{ pickFolderOnOpen?: boolean }>()
const open = defineModel<boolean>({ required: true })
const emit = defineEmits<{ (e: 'created', domain: string): void }>()

const { t } = useI18n()
const sitesStore = useSitesStore()
const siteApplyStore = useSiteApplyStore()
const siteState = useSiteStateStore()

const formRef = ref<FormInstance>()
const domainInput = ref<{ focus?: () => void } | null>(null)
const creating = ref(false)
const showBrowser = ref(false)

const emptyForm = () => ({
  domain: '',
  documentRoot: '',
  runtime: 'php' as Runtime,
  phpVersion: '',
  nodePort: 3000,
  nodeStartCommand: '',
  sslEnabled: true,
  cloudflareTunnel: false,
  createDb: false,
  dbName: '',
  template: '',
  aliases: [] as string[],
  bindAddresses: ['*'],
})
const form = reactive(emptyForm())

// ── Folder: suggestion from the domain, existence check ─────────────
const docRootPlaceholder = ref('C:\\work\\htdocs\\myapp')
const docRootTouched = ref(false)

async function resolveDocRootPlaceholder() {
  try {
    const sys = await fetchSystem()
    if (sys.os.tag === 'windows') return
    const home = sys.os.platform?.match(/(?:Users|home)\/([^/]+)/)?.[1]
    docRootPlaceholder.value = sys.os.tag === 'linux'
      ? (home ? `/home/${home}/Sites/myapp` : '/srv/www/myapp')
      : (home ? `/Users/${home}/Sites/myapp` : '/Sites/myapp')
  } catch { /* keep the Windows example */ }
}

const templateFields = computed(() => templates.find(tpl => tpl.id === form.template)?.fields)

function suggestedFolder(): string {
  const domain = form.domain.trim().toLowerCase()
  if (!domain) return ''
  const sep = pathSeparator(docRootPlaceholder.value)
  const base = docRootPlaceholder.value.slice(0, Math.max(docRootPlaceholder.value.lastIndexOf(sep), 0))
  if (!base) return ''
  const suffix = templateFields.value?.docRootSuffix
  return `${base}${sep}${domain}${suffix ? sep + suffix : ''}`
}

watch([() => form.domain, () => form.template], () => {
  if (!docRootTouched.value) form.documentRoot = suggestedFolder()
})

const folderState = useFolderState(toRef(form, 'documentRoot'))
const folderOwner = useFolderOwner(toRef(form, 'documentRoot'))

async function pickFolder() {
  const chosen = await pickNativeFolder(t('sites.selectFolder'), form.documentRoot || undefined)
  if (chosen === undefined) showBrowser.value = true
  else if (chosen) onFolderPicked(chosen)
}

function onFolderPicked(path: string) {
  form.documentRoot = path
  docRootTouched.value = true
  if (!form.domain) {
    const name = path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? ''
    const slug = name.toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/^-+|-+$/g, '')
    if (slug) form.domain = `${slug}.loc`
  }
}

// ── Runtime ─────────────────────────────────────────────────────────
function phpOptionLabel(v: InstalledPhp): string {
  return `PHP ${v.value}${v.isDefault ? ` (${t('sites.list.phpDefault')})` : ''}`
}

function ensurePhpVersion() {
  const list = siteState.phpVersions ?? []
  if (list.length && !list.some(p => p.value === form.phpVersion)) {
    form.phpVersion = (list.find(p => p.isDefault) ?? list[0]).value
  }
}
watch(() => siteState.phpVersions, ensurePhpVersion)

function applyTemplate(id: string) {
  const f = templates.find(tpl => tpl.id === id)?.fields
  // "No template" puts every templated value back to its default.
  form.runtime = f?.runtime ?? (siteState.phpVersions?.length ? 'php' : 'static')
  form.nodePort = f?.nodePort ?? 3000
  form.nodeStartCommand = f?.nodeStartCommand ?? ''
}

// ── Prerequisites shown inline ──────────────────────────────────────
const httpsNeedsSetup = computed(() => {
  const p = siteState.prerequisites
  return !!p && !(p.mkcertBinaryInstalled && p.mkcertCaInstalled)
})

// HTTPS only works once the local certificate authority is trusted. Until then the safe default is
// "off" (a site with HTTPS and no certificate answers with a browser error, or not at all); the
// callout installs the authority and switches HTTPS on. A choice the user made is left alone.
const sslTouched = ref(false)
watch([open, httpsNeedsSetup], ([isOpen, needsSetup]) => {
  if (isOpen && !sslTouched.value) form.sslEnabled = !needsSetup
}, { immediate: true })

async function installHttps() {
  if (await siteState.install('https')) {
    form.sslEnabled = true
    sslTouched.value = true
  }
}

// A database can only be created while the database server runs.
const dbBlocked = computed(() => ['missing', 'stopped', 'starting'].includes(siteState.databaseServer))
watch(dbBlocked, (blocked) => { if (blocked) form.createDb = false })

const dbNameSuggestion = computed(() => {
  const stem = form.domain.trim().toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '')
  return stem ? `${stem}_db` : 'myapp_db'
})

// ── Bind addresses ──────────────────────────────────────────────────
const bindOptions = ref<BindAddressOption[]>([])
const bindLoading = ref(false)

function normalizeBind(values: string[]): string[] {
  const selected = [...new Set(values.map(v => String(v).trim()).filter(Boolean))]
  if (!selected.includes('*')) return selected
  return selected[selected.length - 1] === '*' ? ['*'] : selected.filter(v => v !== '*')
}
function onBindChange(values: string[]) { form.bindAddresses = normalizeBind(values) }

async function loadBindOptions() {
  bindLoading.value = true
  try {
    bindOptions.value = await fetchBindAddressOptions()
    if (!form.bindAddresses.every(v => bindOptions.value.some(o => o.value === v))) form.bindAddresses = ['*']
  } catch { bindOptions.value = [] } finally { bindLoading.value = false }
}

// ── Validation ──────────────────────────────────────────────────────
const LABEL_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/
const rules = computed<FormRules>(() => ({
  domain: [{
    validator: (_r, value: string, cb: (e?: Error) => void) => {
      const d = (value ?? '').trim().toLowerCase()
      const labels = d.split('.')
      if (!d) return cb(new Error(t('sites.new.err.domainRequired')))
      if (/\s/.test(d)) return cb(new Error(t('sites.new.err.domainSpaces')))
      if (d.length > 253 || !labels.every(l => LABEL_RE.test(l) && l.length <= 63)) return cb(new Error(t('sites.new.err.domainInvalid')))
      if (d !== 'localhost' && labels.length < 2) return cb(new Error(t('sites.new.err.domainNoTld')))
      if (sitesStore.sites.some(s => s.domain.toLowerCase() === d)) return cb(new Error(t('sites.new.err.domainExists')))
      cb()
    },
    trigger: ['blur', 'change'],
  }],
  documentRoot: [{ required: true, message: t('sites.new.err.folderRequired'), trigger: ['blur', 'change'] }],
  bindAddresses: [{
    validator: (_r, value: string[], cb: (e?: Error) => void) =>
      cb(normalizeBind(value ?? []).length ? undefined : new Error(t('sitesToast.bindRequired'))),
    trigger: 'change',
  }],
}))

const previewUrl = computed(() => {
  const d = form.domain.trim().toLowerCase()
  const labels = d.split('.')
  if (!d || !labels.every(l => LABEL_RE.test(l))) return ''
  return siteUrl({ domain: d, sslEnabled: form.sslEnabled, httpPort: 80, httpsPort: 443 })
})

// ── Lifecycle ───────────────────────────────────────────────────────
watch(open, (isOpen) => {
  if (!isOpen) return
  void siteState.refresh().then(ensurePhpVersion)
  void loadBindOptions()
  void resolveDocRootPlaceholder()
})

function onOpened() {
  if (props.pickFolderOnOpen) void pickFolder()
  else void nextTick(() => domainInput.value?.focus?.())
}

function reset() {
  Object.assign(form, emptyForm())
  docRootTouched.value = false
  sslTouched.value = false
  formRef.value?.clearValidate()
  ensurePhpVersion()
}

// ── Submit ──────────────────────────────────────────────────────────
async function createDatabase(domain: string): Promise<boolean> {
  const name = form.dbName.trim() || dbNameSuggestion.value
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/databases`, {
      method: 'POST',
      headers: { ...sitesStore.authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    ElMessage.success(t('sitesToast.createdWithDb', { domain, db: name }))
    return true
  } catch {
    ElMessage.warning(t('sitesToast.createdDbFailed', { domain }))
    return false
  }
}

async function submit() {
  if (creating.value) return
  try { await formRef.value?.validate() } catch { return }

  const domain = form.domain.trim().toLowerCase()
  const bind = normalizeBind(form.bindAddresses)
  creating.value = true
  try {
    const payload: Partial<SiteInfo> & { cloudflareTunnel?: boolean } = {
      domain,
      documentRoot: form.documentRoot.trim(),
      phpVersion: form.runtime === 'php' ? form.phpVersion : 'none',
      sslEnabled: form.sslEnabled,
      aliases: form.aliases.map(a => a.trim()).filter(Boolean),
      bindAddress: bind[0] ?? '*',
      bindAddresses: bind,
      localhostLoopbackEnabled: domain === 'localhost',
      ...(form.runtime === 'node'
        ? { nodeUpstreamPort: form.nodePort, nodeStartCommand: form.nodeStartCommand.trim() }
        : {}),
      ...(form.cloudflareTunnel ? { cloudflareTunnel: true } : {}),
    }
    // The save reloads Apache; tracking it keeps the web server banner from calling that a fault.
    const created = await siteApplyStore.track(domain, () => sitesStore.create(payload))

    for (const w of sitesStore.lastCreateWarnings) ElMessage.warning({ message: w, duration: 8000, showClose: true })
    for (const h of sitesStore.lastCreateHints) ElMessage.info({ message: h, duration: 6000, showClose: true })
    // The daemon creates the site without HTTPS when no certificate could be issued.
    if (form.sslEnabled && created?.sslEnabled === false) {
      ElMessage.warning({ message: t('sitesToast.createdSslMissing'), duration: 8000, showClose: true })
    }
    if (form.createDb) await createDatabase(domain)
    else ElMessage.success(t('sitesToast.created', { domain }))

    open.value = false
    emit('created', domain)
  } catch (e) {
    ElMessage.error(t('sitesToast.createFailed', { err: errorMessage(e) }))
  } finally {
    creating.value = false
  }
}
</script>

<style scoped>
.create-lead {
  margin: 0 0 var(--wdc-space-4);
  color: var(--wdc-text-2);
  font-size: 0.87rem;
  line-height: 1.5;
}
.create-form :deep(.el-form-item) { margin-bottom: var(--wdc-space-4); }
.create-form :deep(.el-select),
.create-form :deep(.el-input) { width: 100%; }
.create-form :deep(.el-radio-group) { width: auto; }
.create-form :deep(.el-input-number) { width: 160px; }

.field-note {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--wdc-space-1-5);
  width: 100%;
  margin-top: var(--wdc-space-1-5);
  color: var(--wdc-text-3);
  font-size: 0.8rem;
  line-height: 1.4;
}
.field-note code {
  font-family: var(--wdc-font-mono);
  color: var(--wdc-text);
  overflow-wrap: anywhere;
}
.note-ok { color: var(--wdc-status-running); }
.note-warn { color: var(--wdc-status-starting); }

.path-input :deep(.el-input__inner) { font-family: var(--wdc-font-mono); font-size: 0.85rem; }
.path-input :deep(.el-input-group__append .el-button) { gap: var(--wdc-space-1-5); }

.runtime-extra { width: 100%; margin-top: var(--wdc-space-2-5); }
.php-select { max-width: 260px; }
.runtime-node { display: flex; flex-wrap: wrap; gap: var(--wdc-space-3); }
.mini-label {
  display: grid;
  gap: var(--wdc-space-1);
  color: var(--wdc-text-2);
  font-size: 0.8rem;
}
.mini-grow { flex: 1; min-width: 180px; }

.inline-callout {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-2) var(--wdc-space-3);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface-2);
  color: var(--wdc-text-2);
  font-size: 0.82rem;
}
.inline-callout .el-icon { color: var(--wdc-status-starting); }
.inline-callout .el-button { margin-left: auto; }
.toggle-item { margin-bottom: var(--wdc-space-3-5); }
.toggle-line { display: inline-flex; align-items: center; gap: var(--wdc-space-2-5); cursor: pointer; }
.toggle-label { color: var(--wdc-text); font-size: 0.93rem; font-weight: 500; }
.toggle-item .field-note,
.toggle-item .inline-callout,
.toggle-item .db-name { margin-top: var(--wdc-space-1-5); }
.toggle-item .field-note { padding-left: 0; }
.db-name { max-width: 280px; }

.more-options {
  margin: var(--wdc-space-1) 0 0;
  padding-top: var(--wdc-space-3);
  border-top: 1px solid var(--wdc-border);
}
.more-options > summary {
  width: fit-content;
  margin-bottom: var(--wdc-space-3);
  color: var(--wdc-accent-ink);
  font-size: 0.87rem;
  font-weight: 500;
  cursor: pointer;
}
.more-options > summary:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; border-radius: var(--wdc-radius-xs); }

.submit-proxy { position: absolute; width: 1px; height: 1px; opacity: 0; pointer-events: none; }
.creating-note { margin-right: var(--wdc-space-3); color: var(--wdc-text-2); font-size: 0.82rem; }
</style>

<style>
/* The form is longer than a laptop window: the body scrolls, the buttons stay in view. */
.create-site-dialog .el-dialog__body { max-height: calc(100vh - 220px); overflow-y: auto; }
</style>
