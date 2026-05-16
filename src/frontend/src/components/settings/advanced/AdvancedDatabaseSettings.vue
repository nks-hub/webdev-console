<template>
  <div class="tab-content">
    <p class="tab-desc">{{ $t('settingsDb.tabDesc') }}</p>
    <div v-if="databases.length > 0" class="db-list">
      <div v-for="db in databases" :key="db" class="db-row">
        <span class="db-name">{{ db }}</span>
        <el-button size="small" type="danger" text @click="$emit('drop', db)">
          {{ $t('settingsDb.drop') }}
        </el-button>
      </div>
    </div>
    <el-empty v-else :description="$t('settingsDb.noUserDatabases')" :image-size="48" />
    <div class="db-create">
      <el-input
        :model-value="newDbName"
        :placeholder="$t('settingsDb.newPlaceholder')"
        size="small"
        style="width: 200px"
        @update:model-value="$emit('update:newDbName', String($event))"
      />
      <el-button size="small" type="primary" :disabled="!newDbName" @click="$emit('create')">
        {{ $t('settingsDb.create') }}
      </el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
defineProps<{
  databases: string[]
  newDbName: string
}>()

defineEmits<{
  'update:newDbName': [value: string]
  create: []
  drop: [name: string]
}>()
</script>

<style scoped>
.db-list {
  margin-bottom: 16px;
}

.db-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  border-bottom: 1px solid var(--wdc-border);
}

.db-row:last-child {
  border-bottom: none;
}

.db-name {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.88rem;
  color: var(--wdc-text);
}

.db-create {
  display: flex;
  gap: 8px;
  margin-top: 12px;
  flex-wrap: wrap;
}
</style>
