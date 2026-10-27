import { ref, watch, type Ref } from 'vue'
import { browseFolder } from '../../api/daemon'
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
