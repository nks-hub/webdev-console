<template>
  <div class="raw-output">
    <div class="raw-output-toolbar">
      <el-radio-group v-model="mode" size="small" :aria-label="$t ? $t('deploy.rawOutput.mode') : 'Output mode'">
        <el-radio-button label="text">Text (a11y)</el-radio-button>
        <el-radio-button label="ansi">ANSI</el-radio-button>
      </el-radio-group>
      <el-button size="small" link @click="copyAll">
        <el-icon><CopyDocument /></el-icon> Copy
      </el-button>
    </div>

    <!-- Dual-stream per v2 a11y audit. xterm canvas isn't readable by screen
         readers; the <pre role="log"> mirror gives selectable, searchable,
         AT-friendly access. Default = text (the accessible view) so SR
         users land on the readable surface. -->
    <pre
      v-if="mode === 'text'"
      ref="preRef"
      class="raw-output-text mono"
      role="log"
      aria-live="polite"
      aria-label="Deploy log (text)"
      tabindex="0"
    >{{ stripped }}</pre>

    <pre
      v-else
      ref="ansiRef"
      class="raw-output-ansi mono"
      tabindex="0"
    >{{ joined }}</pre>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { CopyDocument } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'

const props = withDefaults(defineProps<{
  /** Raw stdout/stderr lines, oldest first. */
  lines: string[]
  /** Cap on rendered lines — older lines are dropped from the view (not the array). */
  maxLines?: number
}>(), {
  maxLines: 5000,
})

// v0.1 ships ANSI mode as plain text (the daemon's --ndjson-events mode emits
// JSON, no ANSI codes). When the wdc Diagnostics page later surfaces raw
// stdout, an ansi-to-html parser slots in here without breaking the text path.
const mode = ref<'text' | 'ansi'>('text')

const ANSI_RE = /\x1b\[[0-9;]*m/g

const visible = computed(() => {
  const overflow = props.lines.length - props.maxLines
  return overflow > 0 ? props.lines.slice(overflow) : props.lines
})

const stripped = computed(() => visible.value.map(l => l.replace(ANSI_RE, '')).join('\n'))
const joined = computed(() => visible.value.join('\n'))

const preRef = ref<HTMLElement | null>(null)
const ansiRef = ref<HTMLElement | null>(null)

watch(() => props.lines.length, async () => {
  // Sticky-bottom auto-scroll. Only scroll when the user IS at the bottom
  // (within 16px), so they can scroll up to inspect older output without
  // being yanked back down by every new line.
  await nextTick()
  const el = mode.value === 'text' ? preRef.value : ansiRef.value
  if (!el) return
  const distFromBottom = el.scrollHeight - el.clientHeight - el.scrollTop
  if (distFromBottom < 16) el.scrollTop = el.scrollHeight
})

async function copyAll(): Promise<void> {
  try {
    await navigator.clipboard.writeText(mode.value === 'text' ? stripped.value : joined.value)
    ElMessage.success('Copied to clipboard')
  } catch {
    ElMessage.warning('Copy failed — clipboard access denied')
  }
}
</script>

<style scoped>
.raw-output { display: flex; flex-direction: column; gap: 6px; }
.raw-output-toolbar {
  display: flex; align-items: center; justify-content: space-between;
  gap: 12px;
}
.raw-output-text,
.raw-output-ansi {
  margin: 0;
  padding: 12px;
  background: var(--el-fill-color-darker);
  color: var(--el-text-color-primary);
  border: 1px solid var(--el-border-color);
  border-radius: 4px;
  max-height: 360px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 12px;
  line-height: 1.5;
}
.raw-output-text:focus,
.raw-output-ansi:focus {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 1px;
}
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
</style>
