<template>
  <section class="sites-empty" :aria-labelledby="titleId">
    <h2 :id="titleId" class="empty-title">{{ $t('sites.empty.title') }}</h2>
    <p class="empty-lead">{{ $t('sites.empty.lead') }}</p>
    <div class="empty-actions">
      <el-button type="primary" size="large" @click="emit('create')">{{ $t('sites.empty.createFirst') }}</el-button>
      <el-button size="large" @click="emit('create-existing')">{{ $t('sites.empty.addExisting') }}</el-button>
    </div>

    <h3 class="check-title">{{ $t('sites.empty.checkTitle') }}</h3>
    <ul class="checklist">
      <li v-for="item in items" :key="item.app" class="check-row">
        <el-icon :size="16" class="check-icon" :class="item.ok ? 'is-ok' : 'is-todo'" aria-hidden="true">
          <Check v-if="item.ok" />
          <Close v-else />
        </el-icon>
        <span class="check-name">{{ item.name }}</span>
        <span class="check-status">{{ item.ok ? $t('sites.empty.installed') : $t('sites.empty.missing') }}</span>
        <el-button
          v-if="!item.ok && prerequisites"
          size="small"
          :loading="siteState.installing.has(item.app)"
          @click="siteState.install(item.app)"
        >{{ $t('sites.empty.install') }}</el-button>
      </li>
    </ul>
  </section>
</template>

<script setup lang="ts">
import { computed, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Close } from '@element-plus/icons-vue'
import { useSiteStateStore, type PrerequisiteApp } from '../../stores/siteState'

/** Shown when there is no site yet: what a site needs to run, with an install button for what is missing. */
const emit = defineEmits<{ (e: 'create'): void; (e: 'create-existing'): void }>()
const { t } = useI18n()
const siteState = useSiteStateStore()
const titleId = useId()

const prerequisites = computed(() => siteState.prerequisites)
const items = computed<Array<{ app: PrerequisiteApp; name: string; ok: boolean }>>(() => {
  const p = prerequisites.value
  return [
    { app: 'apache', name: t('sites.empty.apache'), ok: !!p?.apacheInstalled || siteState.webServer !== 'missing' && siteState.webServer !== 'unknown' },
    { app: 'php', name: t('sites.empty.php'), ok: !!p?.phpInstalled || !!siteState.phpVersions?.length },
    { app: 'https', name: t('sites.empty.https'), ok: !!p?.mkcertBinaryInstalled && !!p?.mkcertCaInstalled },
  ]
})
</script>

<style scoped>
.sites-empty {
  display: grid;
  justify-items: start;
  gap: var(--wdc-space-3);
  max-width: 640px;
  padding: var(--wdc-space-6) 0;
}
.empty-title { font-size: 1.44rem; letter-spacing: -0.02em; }
.empty-lead { margin: 0; color: var(--wdc-text-2); line-height: 1.55; }
.empty-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); margin-top: var(--wdc-space-1); }
.empty-actions :deep(.el-button + .el-button) { margin-left: 0; }

.check-title {
  margin-top: var(--wdc-space-5);
  font-size: 0.93rem;
  color: var(--wdc-text-2);
}
.checklist {
  width: 100%;
  margin: 0;
  padding: 0;
  list-style: none;
  border-top: 1px solid var(--wdc-border);
}
.check-row {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
  min-height: 46px;
  padding: var(--wdc-space-2) 0;
  border-bottom: 1px solid var(--wdc-border);
}
.check-icon.is-ok { color: var(--wdc-status-running); }
.check-icon.is-todo { color: var(--wdc-status-error); }
.check-name { flex: 1; color: var(--wdc-text); font-weight: 500; }
.check-status { color: var(--wdc-text-2); font-size: 0.87rem; }
</style>
