// Minimal stand-in for McpServer that captures registerTool() calls so tests
// can list the registered tools and invoke their handlers with schema
// defaults applied, the way the SDK would.

import { z, type ZodRawShape } from 'zod'
import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'

export interface ToolAnnotations {
  readOnlyHint?: boolean
  destructiveHint?: boolean
  idempotentHint?: boolean
  openWorldHint?: boolean
}

export interface ToolConfig {
  title?: string
  description?: string
  inputSchema?: ZodRawShape
  annotations?: ToolAnnotations
}

export interface ToolResult {
  isError?: boolean
  content: { type: string; text: string }[]
}

export interface RegisteredTool {
  name: string
  config: ToolConfig
  handler: (args: Record<string, unknown>, extra: unknown) => Promise<ToolResult>
}

export function fakeServer() {
  const tools = new Map<string, RegisteredTool>()
  const server = {
    registerTool(name: string, config: ToolConfig, handler: RegisteredTool['handler']) {
      if (tools.has(name)) throw new Error(`duplicate tool ${name}`)
      tools.set(name, { name, config, handler })
    },
  } as unknown as McpServer

  async function call(name: string, args: Record<string, unknown> = {}): Promise<ToolResult> {
    const tool = tools.get(name)
    if (!tool) throw new Error(`tool ${name} is not registered`)
    const parsed = z.object(tool.config.inputSchema ?? {}).parse(args)
    return tool.handler(parsed, {})
  }

  return { server, tools, call }
}

/** Text of a tool result, parsed as JSON when it is JSON. */
export function resultBody(result: ToolResult): unknown {
  const text = result.content[0]?.text ?? ''
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}
