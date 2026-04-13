// Database tools — wraps the daemon's MySQL endpoints.
// All endpoints validate the database name against [a-zA-Z0-9_]+ and inject
// the daemon-managed root password via MYSQL_PWD env var, so we just forward.

import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { toolResponse, toolError, ToolTextResult } from '../formatting.js'
import {
  ConfirmYesSchema,
  DatabaseNameSchema,
  ResponseFormat,
  ResponseFormatSchema,
} from '../schemas.js'

async function safe(fn: () => Promise<unknown>, format?: ResponseFormat): Promise<ToolTextResult> {
  try {
    return toolResponse(await fn(), format)
  } catch (err) {
    return toolError(err instanceof Error ? err.message : String(err))
  }
}

export function registerDatabasesTools(server: McpServer, opts: RegisterOptions): void {
  server.registerTool(
    'wdc_list_databases',
    {
      title: 'List MySQL databases',
      description:
        'List all MySQL databases known to the daemon. System schemas ' +
        '(mysql/sys/information_schema/performance_schema) are filtered out.',
      inputSchema: {
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ response_format }) => safe(() => daemonClient.get('/api/databases'), response_format),
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
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ database, response_format }) =>
      safe(
        () => daemonClient.get(`/api/databases/${encodeURIComponent(database)}/tables`),
        response_format,
      ),
  )

  server.registerTool(
    'wdc_query',
    {
      title: 'Execute SQL query',
      description:
        'Execute a SQL query against a local MySQL database.\n\n' +
        'Args:\n' +
        '  database: MySQL database name.\n' +
        '  sql: SQL statement (max 64KB). Use parameterized SQL — the daemon does not interpolate variables.\n\n' +
        'Returns: { columns, rows, rowCount } for SELECT; { rows: [], message } for DDL/DML.',
      inputSchema: {
        database: DatabaseNameSchema,
        sql: z.string().min(1).max(65535).describe('SQL statement to execute (max 64KB)'),
        response_format: ResponseFormatSchema.optional(),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ database, sql, response_format }) =>
      safe(
        () =>
          daemonClient.post(`/api/databases/${encodeURIComponent(database)}/query`, { sql }),
        response_format,
      ),
  )

  if (opts.readonly) return

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
        'Always confirm with the user before calling this tool.',
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
