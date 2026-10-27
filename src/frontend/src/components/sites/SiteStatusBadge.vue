<template>
  <span class="site-status" :class="`ss-${state.kind}`">
    <el-icon :class="{ 'is-loading': state.kind === 'applying' }" :size="14" aria-hidden="true">
      <Loading v-if="state.kind === 'applying'" />
      <Check v-else-if="state.kind === 'running'" />
      <Clock v-else-if="state.kind === 'waiting'" />
      <Close v-else-if="state.kind === 'failed'" />
      <Remove v-else />
    </el-icon>
    <span class="ss-text">{{ label }}</span>
  </span>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Clock, Close, Loading, Remove } from '@element-plus/icons-vue'
import { useSiteStateStore, type SiteState } from '../../stores/siteState'

/** One status vocabulary for every place a site is listed: word + icon + colour, never colour alone. */
const props = defineProps<{ state: SiteState }>()
const { t } = useI18n()
const siteState = useSiteStateStore()

const label = computed(() =>
  props.state.kind === 'failed'
    ? t('sites.state.failed', { step: siteState.stepLabel(props.state.step ?? '') })
    : t(`sites.state.${props.state.kind}`))
</script>

<style scoped>
.site-status {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  min-width: 0;
  font-size: 0.87rem;
  color: var(--wdc-text);
}
.ss-text { overflow-wrap: break-word; }
.ss-running .el-icon { color: var(--wdc-status-running); }
.ss-waiting,
.ss-disabled { color: var(--wdc-text-2); }
.ss-failed { color: var(--wdc-status-error); font-weight: 500; }
.ss-applying { color: var(--wdc-text-2); }
</style>
