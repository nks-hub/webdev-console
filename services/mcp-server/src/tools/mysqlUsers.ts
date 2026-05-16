// MySQL user management tools — wraps /api/plugins/mysql/users* endpoints.
// Account/host/database identifiers are validated client-side with the same
// regexes the daemon enforces in MySqlUserHelper.cs so bad inputs surface
// as schema errors before they ever hit the wire.

import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe } from '../formatting.js'
import { ConfirmYesSchema, DatabaseNameSchema } from '../schemas.js'

const MySqlUserNameSchema = z
  .string()
  .min(1)
  .max(64)
  .regex(/^[A-Za-z0-9_.-]+$/, {
    message: 'userName may contain only letters, digits, underscore, dot, dash',
  })
  .describe("MySQL account name (max 64 chars, [A-Za-z0-9_.-]). Cannot be 'root'.")

const MySqlHostSchema = z
  .string()
  .min(1)
  .max(255)
  .regex(/^[A-Za-z0-9_.:%-]+$/, {
    message: 'host may contain only letters, digits, dot, dash, underscore, percent and colon',
  })
  .describe("MySQL account host. Common values: 'localhost', '127.0.0.1', '%' (any host).")

const MySqlPasswordSchema = z
  .string()
  .max(255)
  .describe(
    'Password for the MySQL account. Empty string is allowed when the daemon ' +
      'permits passwordless accounts; the daemon validates final acceptability.',
  )

const MySqlPrivilegePresetSchema = z
  .enum(['none', 'read', 'readWrite', 'admin'])
  .describe(
    'Privilege preset:\n' +
      "  'none'      — revoke all (issues only FLUSH PRIVILEGES)\n" +
      "  'read'      — SELECT\n" +
      "  'readWrite' — SELECT, INSERT, UPDATE, DELETE, CREATE TEMPORARY TABLES\n" +
      "  'admin'     — ALL PRIVILEGES (full access on that database)",
  )

export function registerMysqlUsersTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_mysql_users',
    {
      title: 'List MySQL user accounts',
      description:
        'List MySQL user accounts known to the local mysqld instance. ' +
        'Returns user, host, authentication plugin, and lock/expiry flags. ' +
        'Empty-name rows (anonymous) are filtered out.\n\n' +
        'Returns: { users: [{ userName, host, plugin, accountLocked, passwordExpired }], attemptedPort, error? }',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.get('/api/plugins/mysql/users')),
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_create_mysql_user',
    {
      title: 'Create MySQL user account',
      description:
        'Create a MySQL user account via `CREATE USER IF NOT EXISTS`. ' +
        'Optionally grants a privilege preset on a single database in the ' +
        'same call. Idempotent on the create step (IF NOT EXISTS).\n\n' +
        'Args:\n' +
        '  userName: Account name (max 64 chars, [A-Za-z0-9_.-]).\n' +
        "  host: Account host ('localhost', '127.0.0.1', '%', etc.).\n" +
        '  password: Password (empty allowed if daemon permits passwordless).\n' +
        '  database: Optional database to grant on after creation.\n' +
        "  privileges: Privilege preset for the optional grant. Defaults to 'readWrite'.",
      inputSchema: {
        userName: MySqlUserNameSchema,
        host: MySqlHostSchema,
        password: MySqlPasswordSchema,
        database: DatabaseNameSchema.optional(),
        privileges: MySqlPrivilegePresetSchema.optional(),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ userName, host, password, database, privileges }) =>
      safe(() =>
        daemonClient.post('/api/plugins/mysql/users', {
          userName,
          host,
          password,
          ...(database ? { database } : {}),
          ...(privileges ? { privileges } : {}),
        }),
      ),
  )

  server.registerTool(
    'wdc_set_mysql_user_password',
    {
      title: 'Set MySQL user password',
      description:
        "Change a MySQL user's password via `ALTER USER ... IDENTIFIED BY ...`. " +
        "Cannot be used for 'root' accounts — use wdc_change_mysql_root_password " +
        'instead so the current password is verified before the change.\n\n' +
        'Args:\n  userName, host, password.',
      inputSchema: {
        userName: MySqlUserNameSchema,
        host: MySqlHostSchema,
        password: MySqlPasswordSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ userName, host, password }) =>
      safe(() =>
        daemonClient.post('/api/plugins/mysql/users/password', { userName, host, password }),
      ),
  )

  server.registerTool(
    'wdc_grant_mysql_user_database',
    {
      title: 'Grant database privileges to MySQL user',
      description:
        'Grant a privilege preset on a single database to an existing MySQL user. ' +
        'Idempotent — re-applies the GRANT regardless of current state.\n\n' +
        'Args:\n  userName, host, database, privileges (preset).',
      inputSchema: {
        userName: MySqlUserNameSchema,
        host: MySqlHostSchema,
        database: DatabaseNameSchema,
        privileges: MySqlPrivilegePresetSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ userName, host, database, privileges }) =>
      safe(() =>
        daemonClient.post('/api/plugins/mysql/users/grants', {
          userName,
          host,
          database,
          privileges,
        }),
      ),
  )

  server.registerTool(
    'wdc_drop_mysql_user',
    {
      title: 'Drop MySQL user account (destructive)',
      description:
        'DESTRUCTIVE: Drop a MySQL user account via `DROP USER IF EXISTS`. ' +
        "Rejected for 'root' accounts. Requires confirm: \"YES\".\n\n" +
        'You MUST show the user the exact userName@host pair before passing confirm="YES".',
      inputSchema: {
        userName: MySqlUserNameSchema,
        host: MySqlHostSchema,
        confirm: ConfirmYesSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ userName, host }) =>
      safe(() => daemonClient.post('/api/plugins/mysql/users/drop', { userName, host })),
  )

  // Root password — change requires the current password and verifies the
  // new one with a SELECT 1 round-trip on the daemon side.
  server.registerTool(
    'wdc_change_mysql_root_password',
    {
      title: 'Change MySQL root password',
      description:
        "Change the MySQL root password. Requires the current password and " +
        'applies ALTER USER to all root@* accounts, then verifies connectivity ' +
        'with the new password before persisting it to the WDC settings.\n\n' +
        'Args:\n  currentPwd: Current root password (use empty string for passwordless).\n' +
        '  newPwd: New password to set.',
      inputSchema: {
        currentPwd: z.string().describe('Current root password (empty string for passwordless)'),
        newPwd: MySqlPasswordSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ currentPwd, newPwd }) =>
      safe(() => daemonClient.post('/api/plugins/mysql/change-password', { currentPwd, newPwd })),
  )

  server.registerTool(
    'wdc_reset_mysql_root_password',
    {
      title: 'Reset MySQL root password without current (destructive)',
      description:
        'DESTRUCTIVE: Reset the MySQL root password without knowing the current ' +
        'one. The daemon stops mysqld, spawns a skip-grant-tables instance, ' +
        'runs ALTER USER, then restarts the normal mysqld. Briefly takes the ' +
        'database offline. Requires confirm: "YES".\n\n' +
        'Args:\n  newPwd: New root password to set.\n  confirm: Must be "YES".',
      inputSchema: {
        newPwd: MySqlPasswordSchema,
        confirm: ConfirmYesSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ newPwd }) =>
      safe(() => daemonClient.post('/api/plugins/mysql/reset-password', { newPwd })),
  )
}
