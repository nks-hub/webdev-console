import { defineStore } from 'pinia'
import { ref } from 'vue'
import { applySite, fetchSiteApplyStatus, type SiteApplyResultDto } from '../api/daemon'

export interface SiteApplyRecord extends SiteApplyResultDto {
  /** When the result arrived (ms since epoch). */
  at: number
}

/**
 * Latest apply outcome per site. Fed by the daemon's `site:applied` SSE
 * event (every save, reapply-all, startup re-apply), by explicit re-applies
 * from the site page and, after a window reload, by the daemon's own record
 * of the last apply (`hydrate`), so the list shows a failed site as failed
 * even when the apply ran before this window connected.
 */
export const useSiteApplyStore = defineStore('siteApply', () => {
  const results = ref<Record<string, SiteApplyRecord>>({})
  const applying = ref<Set<string>>(new Set())

  function record(result: SiteApplyResultDto) {
    if (!result?.domain || !Array.isArray(result.steps)) return
    results.value = { ...results.value, [result.domain]: { ...result, at: result.at ?? Date.now() } }
  }

  /** Loads the daemon's last apply results; a result already seen live is never replaced by an older one. */
  async function hydrate() {
    let list: SiteApplyResultDto[]
    try { list = await fetchSiteApplyStatus() } catch { return }
    const next = { ...results.value }
    for (const r of list) {
      if (!r?.domain || !Array.isArray(r.steps)) continue
      const at = r.at ?? Date.now()
      if ((next[r.domain]?.at ?? 0) < at) next[r.domain] = { ...r, at }
    }
    results.value = next
  }

  function setApplying(domain: string, on: boolean) {
    const next = new Set(applying.value)
    if (on) next.add(domain)
    else next.delete(domain)
    applying.value = next
  }

  /** Marks a site as "being applied" while `work` runs (a save or a re-apply). */
  async function track<T>(domain: string, work: () => Promise<T>): Promise<T> {
    setApplying(domain, true)
    try {
      return await work()
    } finally {
      setApplying(domain, false)
    }
  }

  async function apply(domain: string): Promise<SiteApplyResultDto> {
    return track(domain, async () => {
      const result = await applySite(domain)
      record(result)
      return result
    })
  }

  return { results, applying, record, hydrate, track, apply }
})
