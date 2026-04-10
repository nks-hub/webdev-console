<template>
  <div class="sites-page">
    <div class="sites-header">
      <h2>Sites</h2>
      <el-button type="primary" size="small" @click="showCreate = true">+ New Site</el-button>
    </div>

    <el-table :data="sitesStore.sites" v-loading="sitesStore.loading" stripe @row-click="selectSite">
      <el-table-column prop="domain" label="Domain" min-width="160">
        <template #default="{ row }">
          <span style="font-weight: 600;">{{ row.domain }}</span>
          <div v-if="row.aliases?.length" style="font-size: 0.75rem; color: #94a3b8; margin-top: 2px;">
            {{ row.aliases.join(', ') }}
          </div>
        </template>
      </el-table-column>
      <el-table-column label="DocRoot" min-width="200">
        <template #default="{ row }">
          <span style="font-size: 0.8rem; color: #cbd5e1; font-family: monospace;">{{ row.documentRoot }}</span>
        </template>
      </el-table-column>
      <el-table-column label="PHP" width="70">
        <template #default="{ row }">
          <el-tag size="small" effect="plain">{{ row.phpVersion || '-' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="Framework" width="100">
        <template #default="{ row }">
          <el-tag v-if="row.framework" size="small" type="warning" effect="plain">{{ row.framework }}</el-tag>
          <span v-else style="color: #64748b;">-</span>
        </template>
      </el-table-column>
      <el-table-column label="SSL" width="50" align="center">
        <template #default="{ row }">
          <el-icon :color="row.sslEnabled ? '#22c55e' : '#64748b'"><Lock /></el-icon>
        </template>
      </el-table-column>
      <el-table-column label="Actions" width="140" fixed="right">
        <template #default="{ row }">
          <el-button size="small" text @click.stop="detectFramework(row.domain)">Detect</el-button>
          <el-button size="small" type="danger" text @click.stop="confirmDelete(row.domain)">Delete</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- Site detail drawer -->
    <el-drawer v-model="drawerOpen" :title="selectedSite?.domain" direction="rtl" size="420px">
      <div v-if="selectedSite" class="site-detail">
        <el-form :model="selectedSite" label-position="top" size="small">
          <el-form-item label="Document Root">
            <el-input v-model="selectedSite.documentRoot" />
          </el-form-item>
          <el-form-item label="PHP Version">
            <el-input v-model="selectedSite.phpVersion" placeholder="8.4" />
          </el-form-item>
          <el-form-item label="Aliases (comma-separated)">
            <el-input v-model="aliasesStr" placeholder="www.myapp.loc, dev.myapp.loc" />
          </el-form-item>
          <el-form-item label="Framework">
            <el-input v-model="selectedSite.framework" disabled />
          </el-form-item>
          <el-form-item label="SSL">
            <el-switch v-model="selectedSite.sslEnabled" />
          </el-form-item>
          <el-form-item label="HTTP Port">
            <el-input-number v-model="selectedSite.httpPort" :min="1" :max="65535" />
          </el-form-item>
        </el-form>
        <el-button type="primary" size="small" @click="saveSelected">Save Changes</el-button>
      </div>
    </el-drawer>

    <!-- Create dialog -->
    <el-dialog v-model="showCreate" title="New Site" width="480px">
      <el-form :model="newSite" label-position="top" size="small">
        <el-form-item label="Domain" required>
          <el-input v-model="newSite.domain" placeholder="myapp.loc" />
        </el-form-item>
        <el-form-item label="Document Root" required>
          <el-input v-model="newSite.documentRoot" placeholder="C:\work\htdocs\myapp" />
        </el-form-item>
        <el-form-item label="PHP Version">
          <el-select v-model="newSite.phpVersion" placeholder="Select PHP" style="width: 100%">
            <el-option label="8.4" value="8.4" />
            <el-option label="8.3" value="8.3" />
            <el-option label="8.2" value="8.2" />
            <el-option label="None" value="none" />
          </el-select>
        </el-form-item>
        <el-form-item label="Aliases (comma-separated)">
          <el-input v-model="newSite.aliases" placeholder="www.myapp.loc, dev.myapp.loc" />
        </el-form-item>
        <el-form-item label="SSL">
          <el-switch v-model="newSite.sslEnabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCreate = false">Cancel</el-button>
        <el-button type="primary" :loading="creating" @click="createSite">Create</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { Lock } from '@element-plus/icons-vue'
import { ElMessageBox, ElMessage } from 'element-plus'
import { useSitesStore } from '../../stores/sites'
import type { SiteInfo } from '../../api/types'

const sitesStore = useSitesStore()
const drawerOpen = ref(false)
const selectedSite = ref<SiteInfo | null>(null)
const showCreate = ref(false)
const creating = ref(false)
const newSite = reactive({ domain: '', documentRoot: '', phpVersion: '8.4', aliases: '', sslEnabled: false })

const aliasesStr = computed({
  get: () => selectedSite.value?.aliases?.join(', ') ?? '',
  set: (v: string) => {
    if (selectedSite.value) {
      selectedSite.value.aliases = v.split(',').map(s => s.trim()).filter(Boolean)
    }
  },
})

onMounted(() => { void sitesStore.load() })

function selectSite(row: SiteInfo) {
  selectedSite.value = { ...row, aliases: [...(row.aliases || [])] }
  drawerOpen.value = true
}

async function saveSelected() {
  if (!selectedSite.value) return
  await sitesStore.update(selectedSite.value.domain, selectedSite.value)
  ElMessage.success('Site updated')
  drawerOpen.value = false
}

async function createSite() {
  creating.value = true
  try {
    const payload = {
      domain: newSite.domain,
      documentRoot: newSite.documentRoot,
      phpVersion: newSite.phpVersion,
      sslEnabled: newSite.sslEnabled,
      aliases: newSite.aliases ? newSite.aliases.split(',').map(s => s.trim()).filter(Boolean) : [],
    }
    await sitesStore.create(payload)
    ElMessage.success(`Site ${newSite.domain} created`)
    showCreate.value = false
    newSite.domain = ''; newSite.documentRoot = ''; newSite.phpVersion = '8.4'; newSite.aliases = ''; newSite.sslEnabled = false
  } finally {
    creating.value = false
  }
}

async function detectFramework(domain: string) {
  try {
    const res = await fetch(`http://localhost:5199/api/sites/${domain}/detect-framework`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...sitesStore.authHeaders() },
    })
    const data = await res.json()
    if (data.framework) {
      ElMessage.success(`Detected: ${data.framework}`)
    } else {
      ElMessage.info('No framework detected')
    }
    await sitesStore.load()
  } catch {
    ElMessage.error('Detection failed')
  }
}

async function confirmDelete(domain: string) {
  await ElMessageBox.confirm('Delete this site? This cannot be undone.', 'Warning', { type: 'warning' })
  await sitesStore.remove(domain)
  ElMessage.success('Site deleted')
}
</script>

<style scoped>
.sites-page { padding: 24px; }
.sites-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 20px; }
.sites-header h2 { margin: 0; font-size: 1.2rem; }
.site-detail { display: flex; flex-direction: column; gap: 16px; }
</style>
