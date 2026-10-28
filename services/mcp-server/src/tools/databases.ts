// Database tools — wraps the daemon's MySQL endpoints.
// The daemon's /query endpoint handles both reads and writes against the
// same code path, but at the MCP layer we split it into two tools so the
// `destructiveHint` annotation stays honest: read-only SELECTs won't
// trigger "destructive action" prompts in clients that honor hints.

import { existsSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { isAbsolute } from 'node:path'
import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

import { daemonClient } from '../daemonClient.js'
import type { RegisterOptions } from '../index.js'
import { IntentTokenSchema, intentHeaders } from './confirmation.js'
import { safe, toolError } from '../formatting.js'
import { ConfirmYesSchema, DatabaseNameSchema, capabilityNote } from '../schemas.js'

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
// no "(" follows. The second group are reads with side effects: SELECT … INTO,
// LOAD_FILE (`\bLOAD\b` misses it because '_' is a word character),
// SLEEP/BENCHMARK, named and row locks, sequence advances. The daemon also
// runs the statement in a read-only transaction with a time limit and row cap.
const WRITE_KEYWORDS =
  /\b(?:(?:INSERT|REPLACE)\b(?!\s*\()|(?:UPDATE|DELETE|MERGE|CREATE|DROP|ALTER|TRUNCATE|RENAME|GRANT|REVOKE|CALL|LOAD|HANDLER|OUTFILE|DUMPFILE|INTO|LOAD_FILE|SLEEP|BENCHMARK|GET_LOCK|RELEASE_LOCK|RELEASE_ALL_LOCKS|LOCK|NEXTVAL|SETVAL|SYS_EXEC|SYS_EVAL)\b)/i

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

/** MySQL table name — same character rule the daemon enforces. */
const TableNameSchema = z
  .string()
  .min(1)
  .max(64)
  .regex(/^[a-zA-Z0-9_]+$/, { message: 'Table name allows only letters, digits, and underscores' })
  .describe('Table name ([a-zA-Z0-9_] only)')

/** Largest SQL dump wdc_import_database sends (the daemon buffers it). */
export const MAX_IMPORT_BYTES = 64 * 1024 * 1024

/** Imports and exports to a file are in the daemon's database_admin capability group. */
const DATABASE_ADMIN = capabilityNote('Database server administration')

const AbsolutePathSchema = z
  .string()
  .min(1)
  .max(4096)
  .refine(p => isAbsolute(p), 'Must be an absolute path')
  .refine(p => !p.includes('\0'), 'Must not contain NUL bytes')

/** SQL to import: inline text, or read from a local file. */
export function readImportSql(sql: string | undefined, filePath: string | undefined): string {
  if ((sql === undefined) === (filePath === undefined)) {
    throw new Error('Pass exactly one of sql or filePath.')
  }
  if (sql !== undefined) return sql
  const size = statSync(filePath!).size
  if (size > MAX_IMPORT_BYTES) {
    throw new Error(`${filePath} is ${size} bytes; the limit is ${MAX_IMPORT_BYTES}. Import it in the WDC app instead.`)
  }
  return readFileSync(filePath!, 'utf8')
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

  server.registerTool(
    'wdc_database_size',
    {
      title: 'Database size',
      description:
        'Size of a database (data + indexes).\n\nArgs:\n  database: MySQL database name.\n\n' +
        'Returns: { size } like "12.5 MB".',
      inputSchema: { database: DatabaseNameSchema },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ database }) => safe(() => daemonClient.get(`/api/databases/${encodeURIComponent(database)}/size`)),
  )

  server.registerTool(
    'wdc_table_columns',
    {
      title: 'Table columns',
      description:
        'Columns of a table: name, type, nullability, default, key, extra.\n\n' +
        'Args:\n  database: Database name.\n  table: Table name.\n\nReturns: { columns: [...] }.',
      inputSchema: { database: DatabaseNameSchema, table: TableNameSchema },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ database, table }) =>
      safe(() =>
        daemonClient.get(`/api/databases/v2/${encodeURIComponent(database)}/tables/${encodeURIComponent(table)}/columns`),
      ),
  )

  server.registerTool(
    'wdc_table_indexes',
    {
      title: 'Table indexes',
      description:
        'Indexes of a table: name, columns, uniqueness, type.\n\n' +
        'Args:\n  database: Database name.\n  table: Table name.\n\nReturns: { indexes: [...] }.',
      inputSchema: { database: DatabaseNameSchema, table: TableNameSchema },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ database, table }) =>
      safe(() =>
        daemonClient.get(`/api/databases/v2/${encodeURIComponent(database)}/tables/${encodeURIComponent(table)}/indexes`),
      ),
  )

  server.registerTool(
    'wdc_browse_table',
    {
      title: 'Browse table rows',
      description:
        'Read one page of rows from a table. For filtering use wdc_query with a SELECT.\n\n' +
        'Args:\n' +
        '  database: Database name.\n  table: Table name.\n' +
        '  page: 1-based page (default 1).\n  pageSize: Rows per page (1-500, default 50).\n' +
        '  orderBy: Column to sort by (ignored if it is not a column).\n  orderDir: asc or desc.\n\n' +
        'Returns: { columns, rows, totalRows, page, pageSize, executionTimeMs, appliedOrderBy, appliedOrderDir }.',
      inputSchema: {
        database: DatabaseNameSchema,
        table: TableNameSchema,
        page: z.number().int().min(1).default(1),
        pageSize: z.number().int().min(1).max(500).default(50),
        orderBy: TableNameSchema.optional().describe('Column name to sort by'),
        orderDir: z.enum(['asc', 'desc']).default('asc'),
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ database, table, page, pageSize, orderBy, orderDir }) => {
      // No free-form WHERE: the daemon splices it into the SQL, which would
      // turn this read-only tool into an unchecked SQL channel.
      const qs = new URLSearchParams({ page: String(page), pageSize: String(pageSize), orderDir })
      if (orderBy) qs.set('orderBy', orderBy)
      return safe(() =>
        daemonClient.get(
          `/api/databases/v2/${encodeURIComponent(database)}/tables/${encodeURIComponent(table)}/data?${qs.toString()}`,
        ),
      )
    },
  )

  if (opts.readonly) return

  server.registerTool(
    'wdc_export_database',
    {
      title: 'Export database (mysqldump)',
      description:
        'Dump a database with mysqldump. With outputPath the dump is written to that file and only its ' +
        'size is returned; without it the SQL text is returned (cut off when large).\n\n' +
        'Args:\n' +
        '  database: Database name.\n' +
        '  outputPath: Absolute path of a new .sql file to write (refuses to overwrite unless overwrite is true). ' +
        `Writing to a file: ${DATABASE_ADMIN}\n` +
        '  overwrite: Replace an existing outputPath.\n\n' +
        'Returns: { database, path, bytes } with outputPath, otherwise the SQL text.',
      inputSchema: {
        database: DatabaseNameSchema,
        outputPath: AbsolutePathSchema.optional(),
        overwrite: z.boolean().default(false),
      },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ database, outputPath, overwrite }) =>
      safe(async () => {
        if (outputPath && !overwrite && existsSync(outputPath)) {
          throw new Error(`${outputPath} already exists; pass overwrite: true to replace it.`)
        }
        // The daemon allows an export to a file only with the database_admin
        // capability, so the target path goes along for it to check.
        const qs = outputPath ? new URLSearchParams({ outputPath }).toString() : ''
        const dump = await daemonClient.get<string>(`/api/databases/${encodeURIComponent(database)}/export?${qs}`)
        const text = typeof dump === 'string' ? dump : JSON.stringify(dump)
        if (!outputPath) return text
        writeFileSync(outputPath, text, 'utf8')
        return { database, path: outputPath, bytes: Buffer.byteLength(text, 'utf8') }
      }),
  )

  server.registerTool(
    'wdc_execute',
    {
      title: 'Execute DDL/DML SQL (destructive)',
      description:
        'Execute a mutating SQL statement (INSERT/UPDATE/DELETE/DDL) against a local ' +
        'MySQL database. Requires confirm: "YES" because statements can destroy data ' +
        'irreversibly.\n\n' +
        'Needs the user\'s approval: wdc_request_confirmation(operation: "database_execute", target: database), ' +
        'then pass the approved intentToken.\n\n' +
        'Args:\n' +
        '  database: MySQL database name.\n' +
        '  sql: SQL statement.\n' +
        '  confirm: Must be "YES".\n' +
        '  intentToken: Approved intent.\n\n' +
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
        'Needs the user\'s approval: wdc_request_confirmation(operation: "database_drop", target: name), ' +
        'then pass the approved intentToken.\n\n' +
        'Args:\n  name: Database to drop.\n  confirm: Must be "YES".\n  intentToken: Approved intent.\n\n' +
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
        // A second call needs a new approval and then finds no database.
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ name, intentToken }) =>
      safe(() => daemonClient.delete(`/api/databases/${encodeURIComponent(name)}`, intentHeaders(intentToken))),
  )

  server.registerTool(
    'wdc_import_database',
    {
      title: 'Import a SQL dump into a database (destructive)',
      description:
        'DESTRUCTIVE: Run a SQL dump against a database with the mysql client. Tables in the dump ' +
        'replace existing ones. Export the database first if its data matters.\n\n' +
        `${DATABASE_ADMIN} Each import needs the user's approval: ` +
        'wdc_request_confirmation(operation: "database_import", target: database).\n\n' +
        'Args:\n' +
        '  database: Target database (must exist; see wdc_create_database).\n' +
        '  sql: The SQL text, or\n' +
        `  filePath: Absolute path of a local .sql file (max ${MAX_IMPORT_BYTES / 1024 / 1024} MB).\n` +
        '  confirm: Must be "YES".\n  intentToken: Approved intent.\n\n' +
        'You MUST tell the user which database will be overwritten and from what before passing confirm="YES".\n\n' +
        'Returns: { ok, message }.',
      inputSchema: {
        database: DatabaseNameSchema,
        sql: z.string().min(1).max(MAX_IMPORT_BYTES).optional(),
        filePath: AbsolutePathSchema.optional(),
        confirm: ConfirmYesSchema,
        intentToken: IntentTokenSchema,
      },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
    },
    async ({ database, sql, filePath, intentToken }) =>
      safe(async () => {
        const text = readImportSql(sql, filePath)
        return daemonClient.postText(
          `/api/databases/${encodeURIComponent(database)}/import`,
          text,
          'text/plain; charset=utf-8',
          intentHeaders(intentToken),
        )
      }),
  )
}
