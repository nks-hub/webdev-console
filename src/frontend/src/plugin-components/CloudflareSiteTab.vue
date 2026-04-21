<template>
  <!-- F91.6: Cloudflare plugin's SiteEdit tab. Kept intentionally compact —
       the full tunnel/DNS editor lives at /cloudflare, this tab exposes
       the per-site toggle + deep-link so the plugin still owns a SiteEdit
       surface without duplicating ~130 lines of state management. -->
  <el-tab-pane :name="name">
    <template #label>
      <span class="tab-label"><el-icon><Link /></el-icon> {{ label }}</span>
    </template>
    <div class="tab-content">
      <section class="edit-card">
        <header class="edit-card-header">
          <span class="edit-card-title">Cloudflare Tunnel</span>
          <span class="edit-card-hint">Per-site public hostname</span>
        </header>
        <div class="edit-card-body">
          <div class="ssl-toggle-row">
            <div class="ssl-toggle-meta">
              <div class="ssl-toggle-title">Expose via tunnel</div>
              <div class="ssl-toggle-desc">
                Creates a proxied CNAME on your Cloudflare zone and adds an ingress
                rule so the site is reachable over HTTPS without port-forwarding.
              </div>
            </div>
            <el-switch
              :model-value="Boolean(site?.cloudflare?.enabled)"
              size="large"
              @update:model-value="onToggle"
            />
          </div>
          <div class="cf-actions">
            <el-button size="small" @click="goToCloudflarePage">
              {{ $t('nav.tunnel') }}
            </el-button>
            <span v-if="site?.cloudflare?.subdomain" class="hint mono">
              {{ site.cloudflare.subdomain }}.{{ site.cloudflare.zoneName }}
            </span>
          </div>
        </div>
      </section>
    </div>
  </el-tab-pane>
</template>

<script setup lang="ts">
import { Link } from '@element-plus/icons-vue'
import { useRouter } from 'vue-router'
import type { SiteInfo } from '../api/types'

const props = defineProps<{
  name: string
  label: string
  site?: SiteInfo | null
}>()

const emit = defineEmits<{
  (e: 'update:site', site: SiteInfo): void
  (e: 'dirty'): void
}>()

const router = useRouter()

function onToggle(v: unknown) {
  if (!props.site) return
  const next: SiteInfo = {
    ...props.site,
    cloudflare: { ...(props.site.cloudflare ?? {}), enabled: Boolean(v) },
  }
  emit('update:site', next)
  emit('dirty')
}

function goToCloudflarePage() {
  void router.push('/cloudflare')
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
.ssl-toggle-title { font-weight: 600; color: var(--wdc-text); }
.ssl-toggle-desc { font-size: 0.82rem; color: var(--wdc-text-3); margin-top: 2px; }
.cf-actions { display: flex; align-items: center; gap: 12px; padding: 10px 0 0; }
.hint { font-size: 0.78rem; color: var(--wdc-text-3); }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
</style>
