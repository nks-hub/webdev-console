import { defineStore } from 'pinia'
import { ref } from 'vue'
import { applySite, type SiteApplyResultDto } from '../api/daemon'

export interface SiteApplyRecord extends SiteApplyResultDto {
  /** When the result arrived (ms since epoch). */
  at: number
}

/**
 * Latest apply outcome per site. Fed by the daemon's `site:applied` SSE
 * event (every save, reapply-all, startup re-apply) and by explicit
 * re-applies from the site page, so the pipeline view shows what the last
 * apply did no matter what triggered it.
 */
export const useSiteApplyStore = defineStore('siteApply', () => {
  const results = ref<Record<string, SiteApplyRecord>>({})
  const applying = ref<Set<string>>(new Set())

  function record(result: SiteApplyResultDto) {
    if (!result?.domain || !Array.isArray(result.steps)) return
    results.value = { ...results.value, [result.domain]: { ...result, at: Date.now() } }
  }

  async function apply(domain: string): Promise<SiteApplyResultDto> {
    applying.value = new Set(applying.value).add(domain)
    try {
      const result = await applySite(domain)
      record(result)
      return result
    } finally {
      const next = new Set(applying.value)
      next.delete(domain)
      applying.value = next
    }
  }

  return { results, applying, record, apply }
})
