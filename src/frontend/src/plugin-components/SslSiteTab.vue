<template>
  <!-- F91.6: SSL plugin's SiteEdit tab — extracted from SiteEdit.vue so the
       plugin itself owns this UI via schema.ContributeSiteEditTab(). When
       the SSL plugin is disabled the tab vanishes; no SiteEdit template
       change was needed, the <PluginSlot> just won't render this anymore. -->
  <el-tab-pane :name="name">
    <template #label>
      <span class="tab-label"><el-icon><Lock /></el-icon> {{ label }}</span>
    </template>
    <div class="tab-content">
      <section class="edit-card">
        <header class="edit-card-header">
          <span class="edit-card-title">HTTPS</span>
          <span class="edit-card-hint">Local certificates via mkcert</span>
        </header>
        <div class="edit-card-body">
          <div class="ssl-toggle-row">
            <div class="ssl-toggle-meta">
              <div class="ssl-toggle-title">Enable HTTPS</div>
              <div class="ssl-toggle-desc">
                Generates a locally-trusted certificate and binds an HTTPS vhost on port {{ site?.httpsPort || 443 }}.
              </div>
            </div>
            <el-switch :model-value="site?.sslEnabled ?? false" size="large" @update:model-value="onSslChange" />
          </div>
          <div v-if="site?.sslEnabled" class="ssl-toggle-row">
            <div class="ssl-toggle-meta">
              <div class="ssl-toggle-title">HTTP → HTTPS redirect</div>
              <div class="ssl-toggle-desc">
                Automatically redirect plain HTTP requests to the HTTPS version.
              </div>
            </div>
            <el-switch :model-value="redirectHttps" size="large" @update:model-value="onRedirectChange" />
          </div>
        </div>
      </section>
    </div>
  </el-tab-pane>
</template>

<script setup lang="ts">
import { Lock } from '@element-plus/icons-vue'
import type { SiteInfo } from '../api/types'

// Plugin-contributed props (from C# Contribute()) + page context (domain, site)
// are merged by <PluginSlot> before being passed here.
const props = defineProps<{
  name: string      // tab id (from plugin props)
  label: string     // tab label (from plugin props)
  site?: SiteInfo | null   // from page context
  redirectHttps?: boolean  // from page context
}>()

const emit = defineEmits<{
  (e: 'update:site', site: SiteInfo): void
  (e: 'update:redirectHttps', value: boolean): void
  (e: 'dirty'): void
}>()

function onSslChange(v: unknown) {
  if (!props.site) return
  emit('update:site', { ...props.site, sslEnabled: Boolean(v) })
  emit('dirty')
}
function onRedirectChange(v: unknown) {
  emit('update:redirectHttps', Boolean(v))
  emit('dirty')
}
</script>

<style scoped>
.tab-content { padding: 16px 0; }
.edit-card { background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); margin-bottom: 16px; }
.edit-card-header { padding: 12px 16px; border-bottom: 1px solid var(--wdc-border); display: flex; justify-content: space-between; align-items: baseline; }
.edit-card-title { font-weight: 700; color: var(--wdc-text); }
.edit-card-hint { font-size: 0.78rem; color: var(--wdc-text-3); }
.edit-card-body { padding: 12px 16px; }
.ssl-toggle-row { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 10px 0; border-bottom: 1px solid var(--wdc-border); }
.ssl-toggle-row:last-child { border-bottom: none; }
.ssl-toggle-title { font-weight: 600; color: var(--wdc-text); }
.ssl-toggle-desc { font-size: 0.82rem; color: var(--wdc-text-3); margin-top: 2px; }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
</style>
