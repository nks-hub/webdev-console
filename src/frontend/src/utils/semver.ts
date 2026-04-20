/** Compare two semver strings. Returns 1 if a > b, -1 if a < b, 0 if equal.
 *  Handles pre-release suffixes per semver spec: 1.0.0 > 1.0.0-beta.1.
 *
 *  Previously two near-identical copies lived in updates-store and
 *  Settings.vue, both with the same bug — the old split-on-'.' logic
 *  parsed "0-beta" as 0 and then ranked 1.0.0-beta.1 ABOVE 1.0.0. Harmless
 *  while we only consume /releases/latest (GitHub excludes pre-releases
 *  there) but trivial to get right so future callers don't inherit the
 *  trap. Extracted here so there's one canonical implementation. */
export function compareSemver(a: string, b: string): number {
  const [aMain, aPre = ''] = a.split('-', 2)
  const [bMain, bPre = ''] = b.split('-', 2)
  const pa = aMain.split('.').map((x) => parseInt(x, 10) || 0)
  const pb = bMain.split('.').map((x) => parseInt(x, 10) || 0)
  const len = Math.max(pa.length, pb.length)
  for (let i = 0; i < len; i++) {
    const da = pa[i] ?? 0
    const db = pb[i] ?? 0
    if (da > db) return 1
    if (da < db) return -1
  }
  // Numerics tied — stable (no pre-release) outranks pre-release.
  if (!aPre && bPre) return 1
  if (aPre && !bPre) return -1
  if (!aPre && !bPre) return 0
  // Both pre-release: lexical compare (beta.2 > beta.1 > alpha.1).
  return aPre.localeCompare(bPre)
}
