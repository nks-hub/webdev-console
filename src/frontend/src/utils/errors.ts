/**
 * Render an unknown caught value as a human-readable string.
 *
 * The renderer's 19 fetch call-sites in Settings.vue (plus a dozen
 * scattered across other pages) all do the same dance:
 *   } catch (e: any) {
 *     ElMessage.error(`X failed: ${e?.message ?? String(e)}`)
 *   }
 * Centralising it means:
 *   1. Call-sites can drop `(e: any)` → `(e: unknown)` (or omit the
 *      annotation entirely) without losing the message extraction.
 *   2. Non-Error throws (strings, numbers, Promise.reject(42)) pass
 *      through String() instead of rendering `undefined`.
 *   3. One place to tweak if we decide to add stack-trace suffixes in
 *      dev mode or similar.
 */
export function errorMessage(err: unknown): string {
  if (err instanceof Error) return err.message
  if (typeof err === 'string') return err
  if (err && typeof err === 'object' && 'message' in err && typeof err.message === 'string') {
    return err.message
  }
  return String(err ?? '')
}
