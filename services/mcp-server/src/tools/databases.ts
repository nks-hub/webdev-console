// Database tools — wraps the daemon's MySQL endpoints.
// The daemon's /query endpoint handles both reads and writes against the
// same code path, but at the MCP layer we split it into two tools so the
// `destructiveHint` annotation stays honest: read-only SELECTs won't
// trigger "destructive action" prompts in clients that honor hints.

import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { safe, toolError } from '../formatting.js'
import { ConfirmYesSchema, DatabaseNameSchema } from '../schemas.js'

const READ_ONLY_SQL = /^\s*(SELECT|SHOW|EXPLAIN|DESC(RIBE)?|WITH)\b/i

export function registerDatabasesTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_databases',
    {
      title: 'List MySQL databases',
      description:
        'List all MySQL databases known to the daemon. System schemas ' +
        '(mysql/sys/information_schema/performance_schema) are filtered out.',
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => safe(() => daemonClient.get('/api/databases')),
  )

  server.registerTool(
    'wdc_database_tables',
    {
      title: 'List tables in database',
      description:
        'List tables in a database with row count and size (MB).\n\n' +
        'Args:\n  database: MySQL database name.\n\n' +
        'Returns: Array of { name, rows, size }.',
      inputSchema: {
        database: DatabaseNameSchema,
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ database }) =>
      safe(() => daemonClient.get(`/api/databases/${encodeURIComponent(database)}/tables`)),
  )

  server.registerTool(
    'wdc_query',
    {
      title: 'SELECT / SHOW query (read-only)',
      description:
        'Execute a read-only SQL query (SELECT / SHOW / EXPLAIN / DESCRIBE / WITH). ' +
        'DDL/DML is rejected at the MCP layer — use wdc_execute for writes.\n\n' +
        'Args:\n' +
        '  database: MySQL database name.\n' +
        '  sql: SQL statement (max 64KB). Use parameterized SQL — the daemon does not interpolate variables.\n\n' +
        'Returns: { columns, rows, rowCount }.',
      inputSchema: {
        database: DatabaseNameSchema,
        sql: z
          .string()
          .min(1)
          .max(65535)
          .describe('Read-only SQL: SELECT / SHOW / EXPLAIN / DESCRIBE / WITH'),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ database, sql }) => {
      if (!READ_ONLY_SQL.test(sql)) {
        return toolError(
          'wdc_query accepts read-only statements only (SELECT / SHOW / EXPLAIN / DESCRIBE / WITH). ' +
            'Use wdc_execute for INSERT / UPDATE / DELETE / DDL.',
        )
      }
      return safe(() =>
        daemonClient.post(`/api/databases/${encodeURIComponent(database)}/query`, { sql }),
      )
    },
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_execute',
    {
      title: 'Execute DDL/DML SQL (destructive)',
      description:
        'Execute a mutating SQL statement (INSERT/UPDATE/DELETE/DDL) against a local ' +
        'MySQL database. Requires confirm: "YES" because statements can destroy data ' +
        'irreversibly.\n\n' +
        'Args:\n' +
        '  database: MySQL database name.\n' +
        '  sql: SQL statement.\n' +
        '  confirm: Must be "YES".\n\n' +
        'Returns: { rows: [], message } with the daemon result.\n\n' +
        'You MUST show the user the exact SQL and the target database before passing confirm="YES".',
      inputSchema: {
        database: DatabaseNameSchema,
        sql: z.string().min(1).max(65535).describe('SQL statement to execute (max 64KB)'),
        confirm: ConfirmYesSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ database, sql }) =>
      safe(() =>
        daemonClient.post(`/api/databases/${encodeURIComponent(database)}/query`, { sql }),
      ),
  )

  server.registerTool(
    'wdc_create_database',
    {
      title: 'Create MySQL database',
      description:
        'Create a new MySQL database via `CREATE DATABASE IF NOT EXISTS`.\n\n' +
        'Args:\n  name: Database name (max 64 chars, [a-zA-Z0-9_] only).',
      inputSchema: {
        name: DatabaseNameSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ name }) => safe(() => daemonClient.post('/api/databases', { name })),
  )

  server.registerTool(
    'wdc_drop_database',
    {
      title: 'Drop MySQL database (destructive)',
      description:
        'DESTRUCTIVE: Drop a MySQL database with all its tables.\n\n' +
        'Args:\n  name: Database to drop.\n  confirm: Must be "YES".\n\n' +
        'You MUST show the user the exact database name and tables it contains ' +
        '(call wdc_database_tables first) before passing confirm="YES".',
      inputSchema: {
        name: DatabaseNameSchema,
        confirm: ConfirmYesSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ name }) =>
      safe(() => daemonClient.delete(`/api/databases/${encodeURIComponent(name)}`)),
  )
}
