import { computed, ref, watch, type Ref } from 'vue'
import { browseFolder } from '../../api/daemon'
import { useSitesStore } from '../../stores/sites'
import { errorMessage } from '../../utils/errors'

/**
 * Tells whether a folder typed or picked in a form exists. Only the daemon's
 * "not a directory" answer counts as "missing": a failed request says nothing
 * about the folder, so the state stays empty then.
 */
export function useFolderState(path: Ref<string>) {
  const state = ref<'' | 'exists' | 'missing'>('')
  let timer: ReturnType<typeof setTimeout> | undefined

  watch(path, (value) => {
    clearTimeout(timer)
    state.value = ''
    const wanted = value?.trim()
    if (!wanted) return
    timer = setTimeout(async () => {
      try {
        await browseFolder(wanted)
        if (wanted === path.value?.trim()) state.value = 'exists'
      } catch (e) {
        if (wanted === path.value?.trim() && /not a directory/i.test(errorMessage(e))) state.value = 'missing'
      }
    }, 350)
  }, { immediate: true })

  return state
}

// shortcut: paths compare case-insensitively (right on Windows and macOS), upgrade for case-sensitive Linux volumes.
const samePath = (p: string) => p.trim().replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase()

/** Domain of another site that already serves this folder, or '' when nobody does. */
export function useFolderOwner(path: Ref<string>, ownDomain?: Ref<string | undefined>) {
  const sites = useSitesStore()
  return computed(() => {
    const wanted = samePath(path.value ?? '')
    if (!wanted) return ''
    return sites.sites.find(s => s.domain !== ownDomain?.value && samePath(s.documentRoot) === wanted)?.domain ?? ''
  })
}
