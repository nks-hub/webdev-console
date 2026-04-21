<template>
  <!-- F91.6: generic quick-action / link button plugins can drop into a
       slot. Props: { label, route, icon?, type? } — route navigates via
       Vue Router when clicked. -->
  <el-button :size="size" :type="type" @click="onClick">
    <el-icon v-if="iconComponent" :size="14"><component :is="iconComponent" /></el-icon>
    <span>{{ label }}</span>
  </el-button>
</template>

<script setup lang="ts">
import { computed, markRaw } from 'vue'
import { useRouter } from 'vue-router'
import { Link, Lock, Download, Box, Connection, Files, QuestionFilled } from '@element-plus/icons-vue'

const props = defineProps<{
  label: string
  route?: string
  icon?: string
  type?: '' | 'primary' | 'success' | 'info' | 'warning' | 'danger'
  size?: 'small' | 'default' | 'large'
}>()

const router = useRouter()
const ICONS: Record<string, unknown> = markRaw({ Link, Lock, Download, Box, Connection, Files, QuestionFilled })
const iconComponent = computed(() => props.icon ? ICONS[props.icon] : undefined)

function onClick() {
  if (props.route) void router.push(props.route)
}
</script>
