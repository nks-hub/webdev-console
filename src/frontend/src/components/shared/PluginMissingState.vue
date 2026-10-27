<template>
  <div class="plugin-missing" role="status">
    <el-empty :image-size="72">
      <template #description>
        <h2 class="plugin-missing-title">{{ title }}</h2>
        <p class="plugin-missing-desc">{{ description }}</p>
        <p v-if="disabledManifest" class="plugin-missing-desc">{{ $t('pluginMissing.disabledHint') }}</p>
      </template>
      <el-button v-if="disabledManifest" type="primary" :loading="enabling" @click="enable">
        {{ $t('pluginMissing.enable') }}
      </el-button>
      <el-button v-else type="primary" @click="openMarketplace">
        {{ $t('pluginMissing.openMarketplace') }}
      </el-button>
    </el-empty>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { usePluginsStore } from '../../stores/plugins'
import { errorMessage } from '../../utils/errors'

/**
 * Shown by a tool page whose plugin is not loaded: offers to enable it when it
 * is installed but switched off, otherwise links to the Plugins marketplace.
 */
const props = defineProps<{
  pluginId: string
  title: string
  description: string
}>()
const emit = defineEmits<{ enabled: [] }>()

const { t } = useI18n()
const router = useRouter()
const pluginsStore = usePluginsStore()
const enabling = ref(false)

const disabledManifest = computed(() =>
  pluginsStore.manifests.find(p => p.id === props.pluginId && !p.enabled) ?? null)

async function enable() {
  enabling.value = true
  try {
    await pluginsStore.toggleEnable(props.pluginId)
    emit('enabled')
  } catch (e) {
    ElMessage.error(t('plugins.toast.toggleFailed', { err: errorMessage(e) }))
  } finally {
    enabling.value = false
  }
}

function openMarketplace() {
  void router.push({ path: '/plugins', query: { tab: 'marketplace', q: props.pluginId } })
}

onMounted(() => {
  if (pluginsStore.manifests.length === 0) void pluginsStore.loadAll()
})
</script>

<style scoped>
.plugin-missing {
  padding: var(--wdc-space-6) 0;
}

.plugin-missing-title {
  margin: 0 0 var(--wdc-space-2);
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.plugin-missing-desc {
  margin: 0 auto var(--wdc-space-2);
  max-width: 52ch;
  font-size: 0.86rem;
  color: var(--wdc-text-2);
  line-height: 1.5;
}
</style>
