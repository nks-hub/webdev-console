<template>
  <div class="tab-content">
    <p class="tab-desc">{{ t('settings.ports.description') }}</p>

    <div v-if="pluginPorts.length > 0" class="settings-card plugin-ports">
      <header class="settings-card-header">
        <span class="settings-card-title">{{ t('settings.advancedPorts.pluginPorts') }}</span>
        <span class="settings-card-meta">{{ t('settings.advancedPorts.active', { n: pluginPorts.length }) }}</span>
      </header>
      <div class="settings-card-body">
        <el-form label-position="left" label-width="200px" size="small" class="ports-form">
          <el-form-item
            v-for="p in pluginPorts"
            :key="p.pluginId + ':' + p.key"
            :label="p.label"
          >
            <el-input-number
              :model-value="p.currentPort"
              :min="1"
              :max="65535"
              class="full-control"
              disabled
            />
            <div class="hint">
              <code class="mono">{{ p.pluginId }}</code> · default {{ p.defaultPort }}
            </div>
          </el-form-item>
        </el-form>
      </div>
    </div>

    <el-alert type="info" :closable="false" show-icon class="port-alert">
      <template #title>{{ t('settings.advancedPorts.portAlertTitle') }}</template>
      {{ t('settings.advancedPorts.portAlertDesc') }}
    </el-alert>

    <el-form label-position="left" label-width="200px" size="small" class="ports-form">
      <el-form-item :label="t('settings.ports.httpPort')">
        <el-input-number
          :model-value="ports.http"
          :min="1"
          :max="65535"
          class="full-control"
          @update:model-value="emitPort('http', $event)"
        />
      </el-form-item>
      <el-form-item :label="t('settings.ports.httpsPort')">
        <el-input-number
          :model-value="ports.https"
          :min="1"
          :max="65535"
          class="full-control"
          @update:model-value="emitPort('https', $event)"
        />
      </el-form-item>
      <el-form-item :label="t('settings.ports.mysqlPort')">
        <el-input-number
          :model-value="ports.mysql"
          :min="1"
          :max="65535"
          class="full-control"
          @update:model-value="emitPort('mysql', $event)"
        />
      </el-form-item>
      <el-form-item :label="t('settings.ports.postgresqlPort')">
        <el-input-number
          :model-value="ports.postgresql"
          :min="1"
          :max="65535"
          class="full-control"
          @update:model-value="emitPort('postgresql', $event)"
        />
      </el-form-item>
      <el-form-item :label="t('settings.ports.redisPort')">
        <el-input-number
          :model-value="ports.redis"
          :min="1"
          :max="65535"
          class="full-control"
          @update:model-value="emitPort('redis', $event)"
        />
      </el-form-item>
      <el-form-item :label="t('settings.ports.mailpitSmtp')">
        <el-input-number
          :model-value="ports.mailpitSmtp"
          :min="1"
          :max="65535"
          class="full-control"
          @update:model-value="emitPort('mailpitSmtp', $event)"
        />
      </el-form-item>
      <el-form-item :label="t('settings.ports.mailpitHttp')">
        <el-input-number
          :model-value="ports.mailpitHttp"
          :min="1"
          :max="65535"
          class="full-control"
          @update:model-value="emitPort('mailpitHttp', $event)"
        />
      </el-form-item>
      <el-form-item :label="t('settings.ports.phpFpmBase')">
        <el-input-number
          :model-value="phpFpmBasePort"
          :min="9000"
          :max="9999"
          class="full-control"
          @update:model-value="$emit('update:phpFpmBasePort', Number($event))"
        />
        <div class="hint">{{ t('settings.ports.phpFpmFormula') }}</div>
      </el-form-item>
    </el-form>
  </div>
</template>

<script setup lang="ts">
export interface AdvancedPortValues {
  http: number
  https: number
  mysql: number
  postgresql: number
  redis: number
  mailpitSmtp: number
  mailpitHttp: number
}

export interface PluginPortSummary {
  pluginId: string
  key: string
  label: string
  currentPort: number
  defaultPort: number
}

const props = defineProps<{
  t: (key: string, params?: Record<string, unknown>) => string
  ports: AdvancedPortValues
  pluginPorts: PluginPortSummary[]
  phpFpmBasePort: number
}>()

const emit = defineEmits<{
  'update:port': [key: keyof AdvancedPortValues, value: number]
  'update:phpFpmBasePort': [value: number]
}>()

function emitPort(key: keyof AdvancedPortValues, value: number | undefined): void {
  emit('update:port', key, Number(value ?? props.ports[key]))
}
</script>

<style scoped>
.plugin-ports {
  margin-bottom: 16px;
}

.settings-card-meta {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
}

.ports-form {
  max-width: 480px;
}

.full-control {
  width: 100%;
}

.port-alert {
  margin-bottom: 12px;
  max-width: 560px;
}
</style>
