// Database tools — wraps the daemon's MySQL endpoints.
// All endpoints validate the database name against [a-zA-Z0-9_]+ and inject
// the daemon-managed root password via MYSQL_PWD env var, so we just forward.

import { daemonClient } from '../daemonClient.js'

export const databasesTools = [
  {
    name: 'wdc_list_databases',
    description:
      'List all MySQL databases known to the daemon (system schemas like mysql/sys/information_schema/performance_schema are filtered out).',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: async () => daemonClient.get('/api/databases'),
  },
  {
    name: 'wdc_create_database',
    description:
      'Create a new MySQL database via `CREATE DATABASE IF NOT EXISTS`. Name must match [a-zA-Z0-9_]+ and be ≤64 chars (MySQL hard limit).',
    inputSchema: {
      type: 'object',
      required: ['name'],
      properties: {
        name: { type: 'string', pattern: '^[a-zA-Z0-9_]+$', maxLength: 64 },
      },
      additionalProperties: false,
    },
    handler: async (args: { name: string }) =>
      daemonClient.post('/api/databases', args),
  },
  {
    name: 'wdc_drop_database',
    description:
      'DESTRUCTIVE: Drop a MySQL database with all its tables. Requires confirm: "YES".',
    inputSchema: {
      type: 'object',
      required: ['name', 'confirm'],
      properties: {
        name: { type: 'string', pattern: '^[a-zA-Z0-9_]+$' },
        confirm: { type: 'string', enum: ['YES'] },
      },
      additionalProperties: false,
    },
    handler: async (args: { name: string; confirm: string }) => {
      if (args.confirm !== 'YES') {
        throw new Error('Refusing to drop database without confirm: "YES"')
      }
      return daemonClient.delete(`/api/databases/${encodeURIComponent(args.name)}`)
    },
  },
  {
    name: 'wdc_database_tables',
    description:
      'List tables in a database with row count and size (MB). Returns [{ name, rows, size }].',
    inputSchema: {
      type: 'object',
      required: ['database'],
      properties: {
        database: { type: 'string', pattern: '^[a-zA-Z0-9_]+$' },
      },
      additionalProperties: false,
    },
    handler: async (args: { database: string }) =>
      daemonClient.get(`/api/databases/${encodeURIComponent(args.database)}/tables`),
  },
  {
    name: 'wdc_query',
    description:
      'Execute a SQL query against a local MySQL database. Returns { columns, rows, rowCount } for SELECT or { rows: [], message } for DDL/DML. Database name validated against [a-zA-Z0-9_]+. Use parameterized SQL when possible — the daemon does not interpolate variables.',
    inputSchema: {
      type: 'object',
      required: ['database', 'sql'],
      properties: {
        database: { type: 'string', pattern: '^[a-zA-Z0-9_]+$' },
        sql: { type: 'string', maxLength: 65535 },
      },
      additionalProperties: false,
    },
    handler: async (args: { database: string; sql: string }) =>
      daemonClient.post(`/api/databases/${encodeURIComponent(args.database)}/query`, {
        sql: args.sql,
      }),
  },
] as const
