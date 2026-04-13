// Shared Zod schemas — reused across multiple tool modules to keep
// validation rules centralized.

import { z } from 'zod'

/** Output format selector — every list/get tool accepts this. */
export enum ResponseFormat {
  MARKDOWN = 'markdown',
  JSON = 'json',
}

export const ResponseFormatSchema = z
  .nativeEnum(ResponseFormat)
  .default(ResponseFormat.JSON)
  .describe(
    "Output format: 'json' for structured data (default, recommended for further AI processing) or 'markdown' for human-readable text",
  )

/** Local development domain — must end in a TLD. */
export const DomainSchema = z
  .string()
  .min(3)
  .max(253)
  .regex(/^[a-z0-9][a-z0-9.-]*\.[a-z]{2,}$/, {
    message: "Domain must be lowercase, end in a TLD (e.g. 'myapp.loc')",
  })
  .describe("Local development domain like 'myapp.loc'")

/** MySQL database identifier — strict subset to prevent SQL injection. */
export const DatabaseNameSchema = z
  .string()
  .min(1)
  .max(64)
  .regex(/^[a-zA-Z0-9_]+$/, {
    message: 'Database name allows only letters, digits, and underscores',
  })
  .describe('MySQL database name (max 64 chars, [a-zA-Z0-9_] only)')

/** PHP major.minor version like "8.3". */
export const PhpVersionSchema = z
  .string()
  .regex(/^[0-9]+\.[0-9]+$/, {
    message: "PHP version must be in major.minor format like '8.3'",
  })
  .describe("PHP version in major.minor format like '8.3'")

/** Required confirmation string for destructive operations. */
export const ConfirmYesSchema = z
  .literal('YES')
  .describe(
    'Must be the literal string "YES" to confirm a destructive operation. Always present this requirement to the user before passing.',
  )
