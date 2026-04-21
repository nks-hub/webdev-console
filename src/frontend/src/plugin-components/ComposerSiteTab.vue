<template>
  <!-- F91.6: Composer plugin's SiteEdit tab. Composer already has a full
       SiteComposer.vue component; this wrapper just registers it as a
       plugin contribution so disabling the Composer plugin removes the
       tab from SiteEdit without a template change. -->
  <el-tab-pane v-if="site?.phpVersion" :name="name">
    <template #label>
      <span class="tab-label"><el-icon><Grid /></el-icon> {{ label }}</span>
    </template>
    <div class="tab-content">
      <SiteComposer v-if="domain" :domain="domain" />
    </div>
  </el-tab-pane>
</template>

<script setup lang="ts">
import { Grid } from '@element-plus/icons-vue'
import SiteComposer from '../components/pages/SiteComposer.vue'
import type { SiteInfo } from '../api/types'

defineProps<{
  name: string
  label: string
  domain?: string
  site?: SiteInfo | null
}>()
</script>

<style scoped>
.tab-content { padding: 16px 0; }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
</style>
