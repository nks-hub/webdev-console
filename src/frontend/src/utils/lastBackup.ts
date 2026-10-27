import { ref } from 'vue'
import { fetchBackups } from '../api/daemon'

/**
 * When the newest backup was made. `createdAt` is null when there is none yet;
 * `loaded` stays false while the daemon has not answered, so nobody raises a
 * false "no backup" alarm. Shared by the Overview figures and attention list.
 */
export const lastBackup = ref<{ loaded: boolean; createdAt: number | null }>({ loaded: false, createdAt: null })

export async function loadLastBackup(): Promise<void> {
  try {
    const { backups } = await fetchBackups()
    const times = backups.map(b => new Date(b.createdUtc).getTime()).filter(n => !Number.isNaN(n))
    lastBackup.value = { loaded: true, createdAt: times.length ? Math.max(...times) : null }
  } catch { /* offline or no backup endpoint: keep what we had */ }
}
