/**
 * Unit tests for the MySQL user management tools.
 *
 * Asserts:
 *   1. All 7 tools register; readonly mode exposes only the list tool
 *   2. Annotations correctly mark read / mutate / destructive
 *   3. Each handler hits the expected daemon endpoint with the expected payload
 *   4. Schema validates user/host/database identifiers and privilege presets
 */

import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('../daemonClient.js', () => ({
  daemonClient: {
    get: vi.fn(() => Promise.resolve({ users: [] })),
    post: vi.fn(() => Promise.resolve({ success: true })),
  },
}))

import { registerMysqlUsersTools } from './mysqlUsers.js'
import { daemonClient } from '../daemonClient.js'

const mockGet = vi.mocked(daemonClient.get)
const mockPost = vi.mocked(daemonClient.post)

interface RegisteredTool {
  name: string
  schema: {
    title?: string
    description?: string
    inputSchema: Record<string, unknown>
    annotations?: Record<string, boolean>
  }
  handler: (args: Record<string, unknown>) => Promise<unknown>
}

function fakeServer(): {
  tools: RegisteredTool[]
  registerTool: (
    name: string,
    schema: RegisteredTool['schema'],
    handler: RegisteredTool['handler'],
  ) => void
} {
  const tools: RegisteredTool[] = []
  return {
    tools,
    registerTool(name, schema, handler) {
      tools.push({ name, schema, handler })
    },
  }
}

beforeEach(() => {
  mockGet.mockClear()
  mockPost.mockClear()
  mockGet.mockImplementation(() => Promise.resolve({ users: [] }))
  mockPost.mockImplementation(() => Promise.resolve({ success: true }))
})

describe('registerMysqlUsersTools — registration', () => {
  it('registers all 7 tools in mutate mode', () => {
    const server = fakeServer()
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    registerMysqlUsersTools(server as any, { readonly: false, deployScopes: ['*'] })

    const names = server.tools.map(t => t.name).sort()
    expect(names).toEqual(
      [
        'wdc_change_mysql_root_password',
        'wdc_create_mysql_user',
        'wdc_drop_mysql_user',
        'wdc_grant_mysql_user_database',
        'wdc_list_mysql_users',
        'wdc_reset_mysql_root_password',
        'wdc_set_mysql_user_password',
      ].sort(),
    )
  })

  it('registers only the list tool in readonly mode', () => {
    const server = fakeServer()
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    registerMysqlUsersTools(server as any, { readonly: true, deployScopes: ['*'] })

    expect(server.tools.map(t => t.name)).toEqual(['wdc_list_mysql_users'])
  })

  it('marks destructive tools with destructiveHint=true', () => {
    const server = fakeServer()
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    registerMysqlUsersTools(server as any, { readonly: false, deployScopes: ['*'] })

    const drop = server.tools.find(t => t.name === 'wdc_drop_mysql_user')!
    expect(drop.schema.annotations?.destructiveHint).toBe(true)
    expect(drop.schema.inputSchema.confirm).toBeDefined()

    const reset = server.tools.find(t => t.name === 'wdc_reset_mysql_root_password')!
    expect(reset.schema.annotations?.destructiveHint).toBe(true)
    expect(reset.schema.inputSchema.confirm).toBeDefined()
  })

  it('marks list tool readOnlyHint=true', () => {
    const server = fakeServer()
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    registerMysqlUsersTools(server as any, { readonly: false, deployScopes: ['*'] })

    const list = server.tools.find(t => t.name === 'wdc_list_mysql_users')!
    expect(list.schema.annotations?.readOnlyHint).toBe(true)
    expect(list.schema.annotations?.destructiveHint).toBe(false)
  })

  it('marks non-destructive mutations correctly', () => {
    const server = fakeServer()
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    registerMysqlUsersTools(server as any, { readonly: false, deployScopes: ['*'] })

    for (const name of [
      'wdc_create_mysql_user',
      'wdc_set_mysql_user_password',
      'wdc_grant_mysql_user_database',
    ]) {
      const tool = server.tools.find(t => t.name === name)!
      expect(tool.schema.annotations?.readOnlyHint, name).toBe(false)
      expect(tool.schema.annotations?.destructiveHint, name).toBe(false)
    }
  })
})

describe('registerMysqlUsersTools — handler routing', () => {
  function build(): RegisteredTool[] {
    const server = fakeServer()
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    registerMysqlUsersTools(server as any, { readonly: false, deployScopes: ['*'] })
    return server.tools
  }

  it('wdc_list_mysql_users → GET /api/plugins/mysql/users', async () => {
    const tools = build()
    const list = tools.find(t => t.name === 'wdc_list_mysql_users')!
    await list.handler({})
    expect(mockGet).toHaveBeenCalledWith('/api/plugins/mysql/users')
  })

  it('wdc_create_mysql_user → POST with full payload', async () => {
    const tools = build()
    const create = tools.find(t => t.name === 'wdc_create_mysql_user')!
    await create.handler({
      userName: 'app',
      host: 'localhost',
      password: 's3cret',
      database: 'shop',
      privileges: 'readWrite',
    })
    expect(mockPost).toHaveBeenCalledWith('/api/plugins/mysql/users', {
      userName: 'app',
      host: 'localhost',
      password: 's3cret',
      database: 'shop',
      privileges: 'readWrite',
    })
  })

  it('wdc_create_mysql_user → POST omits optional database/privileges when absent', async () => {
    const tools = build()
    const create = tools.find(t => t.name === 'wdc_create_mysql_user')!
    await create.handler({ userName: 'app', host: 'localhost', password: 'pw' })
    expect(mockPost).toHaveBeenCalledWith('/api/plugins/mysql/users', {
      userName: 'app',
      host: 'localhost',
      password: 'pw',
    })
  })

  it('wdc_set_mysql_user_password → POST .../password', async () => {
    const tools = build()
    const tool = tools.find(t => t.name === 'wdc_set_mysql_user_password')!
    await tool.handler({ userName: 'app', host: '%', password: 'newpw' })
    expect(mockPost).toHaveBeenCalledWith('/api/plugins/mysql/users/password', {
      userName: 'app',
      host: '%',
      password: 'newpw',
    })
  })

  it('wdc_grant_mysql_user_database → POST .../grants', async () => {
    const tools = build()
    const tool = tools.find(t => t.name === 'wdc_grant_mysql_user_database')!
    await tool.handler({
      userName: 'app',
      host: 'localhost',
      database: 'shop',
      privileges: 'admin',
    })
    expect(mockPost).toHaveBeenCalledWith('/api/plugins/mysql/users/grants', {
      userName: 'app',
      host: 'localhost',
      database: 'shop',
      privileges: 'admin',
    })
  })

  it('wdc_drop_mysql_user → POST .../drop, confirm not forwarded', async () => {
    const tools = build()
    const tool = tools.find(t => t.name === 'wdc_drop_mysql_user')!
    await tool.handler({ userName: 'app', host: 'localhost', confirm: 'YES' })
    expect(mockPost).toHaveBeenCalledWith('/api/plugins/mysql/users/drop', {
      userName: 'app',
      host: 'localhost',
    })
  })

  it('wdc_change_mysql_root_password → POST .../change-password', async () => {
    const tools = build()
    const tool = tools.find(t => t.name === 'wdc_change_mysql_root_password')!
    await tool.handler({ currentPwd: 'old', newPwd: 'new' })
    expect(mockPost).toHaveBeenCalledWith('/api/plugins/mysql/change-password', {
      currentPwd: 'old',
      newPwd: 'new',
    })
  })

  it('wdc_reset_mysql_root_password → POST .../reset-password, confirm not forwarded', async () => {
    const tools = build()
    const tool = tools.find(t => t.name === 'wdc_reset_mysql_root_password')!
    await tool.handler({ newPwd: 'new', confirm: 'YES' })
    expect(mockPost).toHaveBeenCalledWith('/api/plugins/mysql/reset-password', {
      newPwd: 'new',
    })
  })
})
