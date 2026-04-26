<template>
  <div class="diff">
    <h4 class="diff-title">Changes since last deploy</h4>
    <div v-if="!diff || diff.commitCount === 0" class="diff-empty">
      <el-icon><InfoFilled /></el-icon>
      <span>No new commits since last deploy</span>
    </div>
    <div v-else class="diff-rows">
      <div class="diff-row">
        <span class="muted">Commits:</span>
        <span class="mono">{{ diff.commitCount }}</span>
      </div>
      <div v-if="diff.composerLockChanged" class="diff-row diff-row--warn">
        <el-icon><WarningFilled /></el-icon>
        <span>composer.lock changed — dependencies will be re-installed on remote</span>
      </div>
      <div v-if="diff.filesChanged" class="diff-row">
        <span class="muted">Files:</span>
        <span class="mono">+{{ diff.filesAdded ?? 0 }} / -{{ diff.filesRemoved ?? 0 }} / ~{{ diff.filesModified ?? 0 }}</span>
      </div>
      <div v-if="diff.commits && diff.commits.length" class="diff-commits">
        <details>
          <summary>Show {{ diff.commits.length }} commits</summary>
          <ul class="diff-commit-list">
            <li v-for="c in diff.commits" :key="c.sha" class="diff-commit-row">
              <code class="mono">{{ c.sha.slice(0, 7) }}</code>
              <span>{{ c.message }}</span>
              <span class="muted">{{ c.author }}</span>
            </li>
          </ul>
        </details>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { InfoFilled, WarningFilled } from '@element-plus/icons-vue'

export interface DeployDiff {
  commitCount: number
  composerLockChanged: boolean
  filesChanged?: boolean
  filesAdded?: number
  filesRemoved?: number
  filesModified?: number
  commits?: { sha: string; message: string; author: string }[]
}

defineProps<{ diff: DeployDiff | null }>()
</script>

<style scoped>
.diff { display: flex; flex-direction: column; gap: 8px; }
.diff-title { margin: 0; font-size: 13px; color: var(--el-text-color-regular); }
.diff-empty { display: flex; align-items: center; gap: 8px; color: var(--el-text-color-secondary); font-size: 13px; }
.diff-rows { display: flex; flex-direction: column; gap: 6px; font-size: 13px; }
.diff-row { display: flex; align-items: center; gap: 8px; }
.diff-row--warn { color: var(--el-color-warning); }
.muted { color: var(--el-text-color-secondary); }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.diff-commits summary { cursor: pointer; color: var(--el-color-primary); font-size: 12px; }
.diff-commit-list { list-style: none; padding: 0; margin: 6px 0 0 0; display: flex; flex-direction: column; gap: 4px; }
.diff-commit-row { display: grid; grid-template-columns: 60px 1fr auto; gap: 8px; font-size: 12px; }
</style>
