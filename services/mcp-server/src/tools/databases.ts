// Database tools — wraps the daemon's MySQL endpoints.
// The daemon's /query endpoint handles both reads and writes against the
// same code path, but at the MCP layer we split it into two tools so the
// `destructiveHint` annotation stays honest: read-only SELECTs won't
// trigger "destructive action" prompts in clients that honor hints.

import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { IntentTokenSchema, intentHeaders } from './confirmation.js'
import { safe, toolError } from '../formatting.js'
import { ConfirmYesSchema, DatabaseNameSchema } from '../schemas.js'

// Allowed leading verbs for wdc_query. Anything else goes through wdc_execute.
const READ_ONLY_VERBS = /^(SELECT|SHOW|EXPLAIN|DESC(RIBE)?|WITH)\b/i

// Strip leading whitespace + comment blocks so payloads like
// `/* trick */ DROP TABLE users` aren't mistaken for read-only, and
// `SELECT 1 /*; DROP ... */` can't smuggle a second statement past the
// semicolon check below.
function stripLeadingCommentsAndWs(sql: string): string {
  let i = 0
  while (i < sql.length) {
    const ch = sql[i]!
    if (ch === ' ' || ch === '\t' || ch === '\r' || ch === '\n') {
      i++
      continue
    }
    // /* ... */ block comment
    if (ch === '/' && sql[i + 1] === '*') {
      const end = sql.indexOf('*/', i + 2)
      if (end < 0) return sql.slice(i) // unterminated — let DB error out
      i = end + 2
      continue
    }
    // -- line comment
    if (ch === '-' && sql[i + 1] === '-') {
      const nl = sql.indexOf('\n', i + 2)
      if (nl < 0) return ''
      i = nl + 1
      continue
    }
    break
  }
  return sql.slice(i)
}

// Reject any non-trailing `;` so multi-statement payloads
// (`SELECT 1; DROP TABLE users`) can't sneak past the verb check.
function hasInnerSemicolon(sql: string): boolean {
  const trimmed = sql.trimEnd().replace(/;+\s*$/, '')
  return /;/.test(trimmed)
}

// Replace quoted strings/identifiers with empty quotes and comments with a
// space, so keyword checks below only see SQL structure ("delete" inside a
// string literal or a `delete` column name must not count).
function stripLiteralsAndComments(sql: string): string {
  let out = ''
  let i = 0
  while (i < sql.length) {
    const ch = sql[i]!
    if (ch === "'" || ch === '"' || ch === '`') {
      let j = i + 1
      while (j < sql.length) {
        // Backslash escapes only exist inside string literals; MySQL identifiers
        // escape a backtick by doubling it.
        if (ch !== '`' && sql[j] === '\\') { j += 2; continue }
        if (sql[j] === ch) {
          if (sql[j + 1] === ch) { j += 2; continue } // doubled quote escape
          break
        }
        j++
      }
      out += ch + ch
      i = j + 1
      continue
    }
    if (ch === '/' && sql[i + 1] === '*') {
      const end = sql.indexOf('*/', i + 2)
      out += ' '
      i = end < 0 ? sql.length : end + 2
      continue
    }
    // MySQL starts a `--` comment only when whitespace follows (`1--1` is
    // arithmetic), so anything else stays visible to the keyword check.
    const dashComment = ch === '-' && sql[i + 1] === '-' && (i + 2 >= sql.length || /\s/.test(sql[i + 2]!))
    if (dashComment || ch === '#') {
      const nl = sql.indexOf('\n', i)
      out += ' '
      i = nl < 0 ? sql.length : nl
      continue
    }
    out += ch
    i++
  }
  return out
}

// A read-only leading verb can still carry a write: MySQL 8 accepts
// `WITH x AS (SELECT 1) DELETE FROM t`, `SELECT … INTO OUTFILE` writes a file,
// and `EXPLAIN ANALYZE` executes the statement it explains. SHOW is exempt
// (`SHOW CREATE TABLE` is a read).
const VERBS_NEEDING_BODY_CHECK = /^(SELECT|WITH|EXPLAIN|DESC(RIBE)?)\b/i
// INSERT() and REPLACE() are also string functions, so they only count when
// no "(" follows.
const WRITE_KEYWORDS =
  /\b(?:(?:INSERT|REPLACE)\b(?!\s*\()|(?:UPDATE|DELETE|MERGE|CREATE|DROP|ALTER|TRUNCATE|RENAME|GRANT|REVOKE|CALL|LOAD|HANDLER|OUTFILE|DUMPFILE)\b)/i

export function isReadOnlySql(sql: string): boolean {
  // MySQL executes the body of /*! … */ and MariaDB of /*M! … */, so a
  // "comment" can carry a statement. Never treat those as read-only.
  if (/\/\*M?!/i.test(sql)) return false
  const normalized = stripLeadingCommentsAndWs(sql)
  if (!READ_ONLY_VERBS.test(normalized)) return false
  if (hasInnerSemicolon(sql)) return false
  if (VERBS_NEEDING_BODY_CHECK.test(normalized) && WRITE_KEYWORDS.test(stripLiteralsAndComments(normalized))) {
    return false
  }
  return true
}

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
      if (!isReadOnlySql(sql)) {
        const executeHint = opts.readonly
          ? 'Mutating SQL is disabled in --readonly mode.'
          : 'Use wdc_execute for INSERT / UPDATE / DELETE / DDL.'
        return toolError(
          'wdc_query accepts a single read-only statement only ' +
            '(SELECT / SHOW / EXPLAIN / DESCRIBE / WITH, no inner semicolons, no leading DML/DDL in a comment). ' +
            executeHint,
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
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ database, sql, intentToken }) =>
      safe(() =>
        daemonClient.post(`/api/databases/${encodeURIComponent(database)}/query`, { sql }, intentHeaders(intentToken)),
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
        intentToken: IntentTokenSchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ name, intentToken }) =>
      safe(() => daemonClient.delete(`/api/databases/${encodeURIComponent(name)}`, intentHeaders(intentToken))),
  )
}
