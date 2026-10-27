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
import { IntentTokenSchema, intentHeaders } from './confirmation.js'

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
  .describe(
    "MySQL account host. Common values: 'localhost', '127.0.0.1', '%' (any host). " +
      "A host other than localhost / 127.0.0.1 / ::1 is database administration (see wdc_create_mysql_user).",
  )

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
      "  'admin'     — ALL PRIVILEGES (full access on that database; database administration)\n" +
      'System schemas (mysql, sys, information_schema, performance_schema) are refused.',
  )

// Creating or granting with the admin preset or for a non-local host.
const ADMIN_ACCOUNT_NOTE =
  "With the MCP token, the 'admin' preset or a host other than localhost / 127.0.0.1 / ::1 needs the " +
  'operator\'s database_admin capability (WDC → Settings → MCP) and the user\'s approval: call ' +
  'wdc_request_confirmation with operation "mysql_user_admin" and target "userName@host", then pass the ' +
  'returned intentToken. read / readWrite for a local account need neither.'

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
        "  privileges: Privilege preset for the optional grant. Defaults to 'readWrite'.\n" +
        '  intentToken: Approved mysql_user_admin intent, when needed.\n\n' +
        ADMIN_ACCOUNT_NOTE,
      inputSchema: {
        userName: MySqlUserNameSchema,
        host: MySqlHostSchema,
        password: MySqlPasswordSchema,
        database: DatabaseNameSchema.optional(),
        privileges: MySqlPrivilegePresetSchema.optional(),
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ userName, host, password, database, privileges, intentToken }) =>
      safe(() =>
        daemonClient.post(
          '/api/plugins/mysql/users',
          {
            userName,
            host,
            password,
            ...(database ? { database } : {}),
            ...(privileges ? { privileges } : {}),
          },
          intentHeaders(intentToken),
        ),
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
        "With the MCP token the daemon needs the user's approval for each account: call " +
        'wdc_request_confirmation with operation "mysql_user_password" and target "userName@host", ' +
        'then pass the returned intentToken.\n\n' +
        'Args:\n  userName, host, password.\n  intentToken: Approved intent from wdc_request_confirmation.',
      inputSchema: {
        userName: MySqlUserNameSchema,
        host: MySqlHostSchema,
        password: MySqlPasswordSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ userName, host, password, intentToken }) =>
      safe(() =>
        daemonClient.post(
          '/api/plugins/mysql/users/password',
          { userName, host, password },
          intentHeaders(intentToken),
        ),
      ),
  )

  server.registerTool(
    'wdc_grant_mysql_user_database',
    {
      title: 'Grant database privileges to MySQL user',
      description:
        'Grant a privilege preset on a single database to an existing MySQL user. ' +
        'Idempotent — re-applies the GRANT regardless of current state.\n\n' +
        'Args:\n  userName, host, database, privileges (preset).\n' +
        '  intentToken: Approved mysql_user_admin intent, when needed.\n\n' +
        ADMIN_ACCOUNT_NOTE,
      inputSchema: {
        userName: MySqlUserNameSchema,
        host: MySqlHostSchema,
        database: DatabaseNameSchema,
        privileges: MySqlPrivilegePresetSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ userName, host, database, privileges, intentToken }) =>
      safe(() =>
        daemonClient.post(
          '/api/plugins/mysql/users/grants',
          { userName, host, database, privileges },
          intentHeaders(intentToken),
        ),
      ),
  )

  server.registerTool(
    'wdc_drop_mysql_user',
    {
      title: 'Drop MySQL user account (destructive)',
      description:
        'DESTRUCTIVE: Drop a MySQL user account via `DROP USER IF EXISTS`. ' +
        "Rejected for 'root' accounts. Requires confirm: \"YES\".\n\n" +
        'You MUST show the user the exact userName@host pair before passing confirm="YES". ' +
        'With the MCP token the daemon also needs the user\'s approval: call wdc_request_confirmation ' +
        'with operation "mysql_user_drop" and target "userName@host", then pass the returned intentToken.',
      inputSchema: {
        userName: MySqlUserNameSchema,
        host: MySqlHostSchema,
        confirm: ConfirmYesSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ userName, host, intentToken }) =>
      safe(() => daemonClient.post('/api/plugins/mysql/users/drop', { userName, host }, intentHeaders(intentToken))),
  )

  // Root password — change requires the current password and verifies the
  // new one with a SELECT 1 round-trip on the daemon side.
  server.registerTool(
    'wdc_change_mysql_root_password',
    {
      title: 'Change MySQL root password',
      description:
        "Change the MySQL root password. Requires the current password and " +
        'applies ALTER USER to root@localhost and root@127.0.0.1, then verifies connectivity ' +
        'with the new password before persisting it to the WDC settings.\n\n' +
        "With the MCP token this needs the operator's database_admin capability (WDC → Settings → MCP) " +
        'and the user\'s approval: call wdc_request_confirmation with operation "mysql_root_reset" first.\n\n' +
        'Args:\n  currentPwd: Current root password (use empty string for passwordless).\n' +
        '  newPwd: New password to set.\n  intentToken: Approved intent from wdc_request_confirmation.',
      inputSchema: {
        currentPwd: z.string().describe('Current root password (empty string for passwordless)'),
        newPwd: MySqlPasswordSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ currentPwd, newPwd, intentToken }) =>
      safe(() =>
        daemonClient.post('/api/plugins/mysql/change-password', { currentPwd, newPwd }, intentHeaders(intentToken)),
      ),
  )

  server.registerTool(
    'wdc_reset_mysql_root_password',
    {
      title: 'Reset MySQL root password without current (destructive)',
      description:
        'DESTRUCTIVE: Reset the MySQL root password without knowing the current ' +
        'one. The daemon stops mysqld, spawns a skip-grant-tables instance, ' +
        'runs ALTER USER, then restarts the normal mysqld. Briefly takes the ' +
        'database offline. Requires confirm: "YES". With the MCP token the daemon also needs the ' +
        'user\'s approval: call wdc_request_confirmation with operation "mysql_root_reset" first.\n\n' +
        'Args:\n  newPwd: New root password to set.\n  confirm: Must be "YES".\n' +
        '  intentToken: Approved intent from wdc_request_confirmation.',
      inputSchema: {
        newPwd: MySqlPasswordSchema,
        confirm: ConfirmYesSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ newPwd, intentToken }) =>
      safe(() => daemonClient.post('/api/plugins/mysql/reset-password', { newPwd }, intentHeaders(intentToken))),
  )
}
