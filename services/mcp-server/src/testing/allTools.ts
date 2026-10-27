// Registers every tool module on a fake server, the way src/index.ts does,
// for tests that look across the whole tool surface.

import type { RegisterOptions } from '../index.js'
import { fakeServer } from './fakeServer.js'
import { registerSitesTools } from '../tools/sites.js'
import { registerServicesTools } from '../tools/services.js'
import { registerSystemTools } from '../tools/system.js'
import { registerDatabasesTools } from '../tools/databases.js'
import { registerMysqlUsersTools } from '../tools/mysqlUsers.js'
import { registerSslTools } from '../tools/ssl.js'
import { registerPhpTools } from '../tools/php.js'
import { registerBinariesTools } from '../tools/binaries.js'
import { registerPluginsTools } from '../tools/plugins.js'
import { registerBackupTools } from '../tools/backup.js'
import { registerSettingsTools } from '../tools/settings.js'
import { registerCloudflareTools } from '../tools/cloudflare.js'
import { registerDeployTools } from '../tools/deploy.js'
import { registerConfirmationTools } from '../tools/confirmation.js'
import { registerComposerTools } from '../tools/composer.js'
import { registerNodeTools } from '../tools/node.js'
import { registerHostsTools } from '../tools/hosts.js'

export function allTools(opts: RegisterOptions = { readonly: false, deployScopes: ['*'] }) {
  const s = fakeServer()
  for (const register of [
    registerSitesTools,
    registerServicesTools,
    registerSystemTools,
    registerDatabasesTools,
    registerMysqlUsersTools,
    registerSslTools,
    registerPhpTools,
    registerBinariesTools,
    registerPluginsTools,
    registerBackupTools,
    registerSettingsTools,
    registerCloudflareTools,
    registerDeployTools,
    registerConfirmationTools,
    registerComposerTools,
    registerNodeTools,
    registerHostsTools,
  ]) {
    register(s.server, opts)
  }
  return s
}
