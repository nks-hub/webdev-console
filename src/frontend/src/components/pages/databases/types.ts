// Shared TS types for the Databases page family. Mirrors the daemon's
// /api/databases/v2/* response shapes (see Data/IDbDriver.cs).

export interface DatabaseSummary {
  name: string
  sizeBytes?: number | null
  charset?: string | null
  collation?: string | null
}

export interface TableSummary {
  name: string
  kind: 'table' | 'view'
  rowsApprox?: number | null
  dataBytes?: number | null
  indexBytes?: number | null
  engine?: string | null
  collation?: string | null
  comment?: string | null
}

export interface ColumnInfo {
  name: string
  type: string
  nullable: boolean
  default?: string | null
  isPrimaryKey: boolean
  isAutoIncrement: boolean
  comment?: string | null
  ordinalPosition: number
}

export interface IndexInfo {
  name: string
  unique: boolean
  primary: boolean
  type: string
  columns: string[]
}

export interface DataColumn {
  name: string
  type: string
  nullable: boolean
  isPrimaryKey: boolean
}

export interface BrowseResult {
  columns: DataColumn[]
  rows: unknown[][]
  totalRows: number
  page: number
  pageSize: number
  executionTimeMs: number
  appliedOrderBy?: string | null
  appliedOrderDir?: string | null
}

export interface QueryResultSet {
  statementText: string
  columns: DataColumn[]
  rows: unknown[][]
  rowsAffected: number
  executionTimeMs: number
}

export interface QueryExecutionResult {
  results: QueryResultSet[]
  executionTimeMs: number
  warnings?: { level: string; code: number; message: string }[]
}

export type DetailView = 'overview' | 'browse' | 'structure' | 'console'
