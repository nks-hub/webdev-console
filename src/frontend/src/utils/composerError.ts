import { DaemonError } from '../api/daemon'
import { errorMessage } from './errors'

const NOT_INSTALLED = 'composer_not_installed'

/** The daemon refused a Composer action with 409 because composer.phar is not installed. */
export function isComposerNotInstalled(err: unknown): boolean {
  return err instanceof DaemonError && err.status === 409 && (err.code === NOT_INSTALLED || err.detail === NOT_INSTALLED)
}

/** Text for a failed Composer call: "not installed" in the language of the app, anything else as the daemon worded it. */
export function composerErrorMessage(err: unknown, t: (key: string) => string): string {
  return isComposerNotInstalled(err) ? t('composer.notInstalledTitle') : errorMessage(err)
}
