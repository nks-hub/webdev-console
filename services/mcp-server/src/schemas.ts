// Shared Zod schemas — reused across multiple tool modules to keep
// validation rules centralized.

import { z } from 'zod'

/** Local development domain — must end in a TLD. */
export const DomainSchema = z
  .string()
  .min(3)
  .max(253)
  // Accept mixed case, normalize to lowercase for the daemon.
  .transform((s) => s.toLowerCase())
  .pipe(
    z
      .string()
      .regex(/^[a-z0-9][a-z0-9.-]*\.[a-z]{2,}$/, {
        message: "Domain must end in a TLD (e.g. 'myapp.loc')",
      }),
  )
  .describe("Local development domain like 'myapp.loc' (case-insensitive)")

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
    'Must be the literal string "YES" to confirm a destructive operation. ' +
      'The assistant MUST show the user exactly what will be affected and ' +
      'receive explicit user confirmation before passing this value.',
  )
