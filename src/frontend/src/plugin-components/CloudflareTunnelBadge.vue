<template>
  <!-- F91.6: per-site-row badge contributed by the Cloudflare plugin to
       the sites-row-badges slot. Shows "{subdomain}.{zone}" as a link
       when the site has cloudflare.enabled, dims when cloudflared is off. -->
  <div
    v-if="site?.cloudflare?.enabled && site.cloudflare.subdomain"
    class="col-tunnel"
    :class="{ 'col-tunnel-offline': !cloudflaredRunning }"
  >
    <svg class="tunnel-icon" viewBox="0 0 20 14" fill="currentColor" width="13" height="13" style="vertical-align: middle">
      <path d="M16 6a4 4 0 0 0-7.74-1.32A3.5 3.5 0 1 0 3.5 11H16a3 3 0 0 0 0-6z"/>
    </svg>
    <a
      :href="`https://${site.cloudflare.subdomain}.${site.cloudflare.zoneName}`"
      target="_blank"
      @click.stop
      :title="cloudflaredRunning
        ? 'Open public URL in browser'
        : 'Tunnel service is stopped — this URL will not respond until you start cloudflared'"
    >{{ site.cloudflare.subdomain }}.{{ site.cloudflare.zoneName }}</a>
    <span v-if="!cloudflaredRunning" class="tunnel-badge-offline">offline</span>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useDaemonStore } from '../stores/daemon'
import type { SiteInfo } from '../api/types'

defineProps<{
  site?: SiteInfo | null
}>()

const daemonStore = useDaemonStore()
const cloudflaredRunning = computed(() =>
  daemonStore.services.some(s => s.id === 'cloudflare' && (s.state === 2 || s.status === 'running'))
)
</script>

<style scoped>
.col-tunnel {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  background: rgba(243, 128, 32, 0.12);
  color: #f38020;
  border: 1px solid rgba(243, 128, 32, 0.3);
  border-radius: 12px;
  font-size: 0.72rem;
}
.col-tunnel-offline { opacity: 0.55; }
.col-tunnel a { color: inherit; text-decoration: none; }
.col-tunnel a:hover { text-decoration: underline; }
.tunnel-badge-offline {
  margin-left: 4px;
  padding: 0 6px;
  border-radius: 8px;
  background: rgba(239, 68, 68, 0.2);
  color: var(--wdc-status-error);
  font-size: 0.64rem;
  font-weight: 700;
  text-transform: uppercase;
}
</style>
