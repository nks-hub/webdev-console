<template>
  <div class="sites-page">
    <div class="sites-header">
      <h2>Sites</h2>
      <el-button type="primary" size="small" @click="showCreate = true">+ New Site</el-button>
    </div>

    <el-table :data="sitesStore.sites" v-loading="sitesStore.loading" stripe @row-click="selectSite">
      <el-table-column prop="domain" label="Domain" min-width="180" />
      <el-table-column label="PHP" width="80">
        <template #default="{ row }">
          <el-tag size="small" effect="plain">{{ row.phpVersion }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="SSL" width="60">
        <template #default="{ row }">
          <el-icon :color="row.sslEnabled ? '#22c55e' : '#64748b'"><Lock /></el-icon>
        </template>
      </el-table-column>
      <el-table-column label="Status" width="90">
        <template #default="{ row }">
          <el-tag :type="row.status === 'active' ? 'success' : 'info'" size="small" effect="dark">
            {{ row.status }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="Actions" width="100" fixed="right">
        <template #default="{ row }">
          <el-button size="small" type="danger" text @click.stop="confirmDelete(row.id)">Delete</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- Site detail drawer -->
    <el-drawer v-model="drawerOpen" :title="selectedSite?.domain" direction="rtl" size="400px">
      <div v-if="selectedSite" class="site-detail">
        <el-form :model="selectedSite" label-position="top" size="small">
          <el-form-item label="PHP Version">
            <el-input v-model="selectedSite.phpVersion" />
          </el-form-item>
          <el-form-item label="Document Root">
            <el-input v-model="selectedSite.docRoot" />
          </el-form-item>
          <el-form-item label="SSL">
            <el-switch v-model="selectedSite.sslEnabled" />
          </el-form-item>
        </el-form>
        <el-button type="primary" size="small" @click="saveSelected">Save Changes</el-button>
      </div>
    </el-drawer>

    <!-- Create dialog -->
    <el-dialog v-model="showCreate" title="New Site" width="400px">
      <el-form :model="newSite" label-position="top" size="small">
        <el-form-item label="Domain">
          <el-input v-model="newSite.domain" placeholder="myapp.loc" />
        </el-form-item>
        <el-form-item label="Document Root">
          <el-input v-model="newSite.docRoot" placeholder="C:\work\sites\myapp\www" />
        </el-form-item>
        <el-form-item label="PHP Version">
          <el-input v-model="newSite.phpVersion" placeholder="8.2" />
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
import { ref, reactive, onMounted } from 'vue'
import { Lock } from '@element-plus/icons-vue'
import { ElMessageBox, ElMessage } from 'element-plus'
import { useSitesStore } from '../../stores/sites'
import type { SiteInfo } from '../../api/types'

const sitesStore = useSitesStore()
const drawerOpen = ref(false)
const selectedSite = ref<SiteInfo | null>(null)
const showCreate = ref(false)
const creating = ref(false)
const newSite = reactive({ domain: '', docRoot: '', phpVersion: '8.2' })

onMounted(() => { void sitesStore.load() })

function selectSite(row: SiteInfo) {
  selectedSite.value = { ...row }
  drawerOpen.value = true
}

async function saveSelected() {
  if (!selectedSite.value) return
  await sitesStore.update(selectedSite.value.id, selectedSite.value)
  ElMessage.success('Site updated')
  drawerOpen.value = false
}

async function createSite() {
  creating.value = true
  try {
    await sitesStore.create(newSite)
    ElMessage.success(`Site ${newSite.domain} created`)
    showCreate.value = false
    newSite.domain = ''; newSite.docRoot = ''; newSite.phpVersion = '8.2'
  } finally {
    creating.value = false
  }
}

async function confirmDelete(id: string) {
  await ElMessageBox.confirm('Delete this site? This cannot be undone.', 'Warning', { type: 'warning' })
  await sitesStore.remove(id)
  ElMessage.success('Site deleted')
}
</script>

<style scoped>
.sites-page { padding: 24px; }
.sites-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 20px; }
.sites-header h2 { margin: 0; font-size: 1.2rem; }
.site-detail { display: flex; flex-direction: column; gap: 16px; }
</style>
