<template>
  <ul style="padding-left: 16px; margin: 0">
    <!-- Renders blockerDetails (?explain=true payload) when available with
         phase tag + remediation hint per blocker. Falls back to summary-only
         rows synthesized from flat blockers[] when older daemon returns
         the pre-iter-17 envelope shape. -->
    <li
      v-for="(b, i) in entries"
      :key="i"
      style="line-height: 1.4; margin-bottom: 6px"
    >
      <span v-if="b.phase" style="display: inline-block; min-width: 28px">
        <el-tag size="small" type="info" effect="plain">{{ b.phase }}</el-tag>
      </span>
      {{ b.summary }}
      <div
        v-if="b.remediation"
        class="muted"
        style="font-size: 11px; margin-top: 2px; padding-left: 32px"
      >
        → {{ b.remediation }}
      </div>
    </li>
  </ul>
</template>

<script setup lang="ts">
import { computed } from 'vue'

interface BlockerDetail {
  summary: string
  phase: string
  remediation: string
}

const props = defineProps<{
  blockers: string[]
  blockerDetails?: BlockerDetail[]
}>()

// When blockerDetails is non-empty (?explain=true), use it directly.
// Otherwise synthesize entries from flat summary strings — graceful
// degrade for older daemons that pre-date iter 17.
const entries = computed<BlockerDetail[]>(() => {
  if (props.blockerDetails && props.blockerDetails.length > 0) {
    return props.blockerDetails
  }
  return props.blockers.map((s) => ({ summary: s, phase: '', remediation: '' }))
})
</script>
