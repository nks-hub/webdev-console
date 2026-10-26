/**
 * Unit tests for the wdc_query read-only guard.
 *
 * The guard is the only thing keeping wdc_query (readOnlyHint, the one SQL
 * tool left in --readonly mode) from running writes through the daemon's
 * /query endpoint, so every bypass found so far is pinned here.
 */

import { describe, it, expect, vi } from 'vitest'

// databases.ts imports the daemon client; keep the test off the port file.
vi.mock('../daemonClient.js', () => ({ daemonClient: {} }))

import { isReadOnlySql } from './databases.js'

describe('isReadOnlySql', () => {
  it.each([
    'SELECT * FROM users',
    'select 1;',
    'SHOW CREATE TABLE users',
    'SHOW VARIABLES LIKE "character_set%"',
    "SELECT * FROM logs WHERE msg = 'delete me'",
    'SELECT `delete`, `update` FROM t',
    "SELECT REPLACE(name, 'a', 'b'), INSERT(name, 1, 2, 'x') FROM t",
    'WITH x AS (SELECT 1 AS n) SELECT n FROM x',
    'DESCRIBE users',
    'EXPLAIN SELECT * FROM users',
    "SELECT 'it''s' FROM t",
    'SELECT 1 -- delete\n',
  ])('allows %s', (sql) => {
    expect(isReadOnlySql(sql)).toBe(true)
  })

  it.each([
    'WITH x AS (SELECT 1) DELETE FROM users',
    'with x as (select id from t) update t set a = 1',
    'WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x',
    "SELECT * FROM users INTO OUTFILE '/tmp/x'",
    "SELECT 'x' INTO DUMPFILE '/tmp/x'",
    'EXPLAIN ANALYZE DELETE FROM users',
    '/* x */ DROP TABLE users',
    'SELECT 1; DROP TABLE users',
    'UPDATE t SET a = 1',
  ])('rejects %s', (sql) => {
    expect(isReadOnlySql(sql)).toBe(false)
  })
})
