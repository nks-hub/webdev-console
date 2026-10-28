/** One DNS label of a site domain: letters, digits and inner hyphens. */
export const DOMAIN_LABEL_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/

export type DomainProblem = '' | 'domainRequired' | 'domainSpaces' | 'domainInvalid' | 'domainNoTld' | 'domainExists'

/**
 * Why a site domain is not acceptable, as the key under `sites.new.err`, or '' when it is.
 * `existing` lists the domains of the sites that already exist.
 */
export function domainProblem(value: string, existing: string[]): DomainProblem {
  const d = (value ?? '').trim().toLowerCase()
  const labels = d.split('.')
  if (!d) return 'domainRequired'
  if (/\s/.test(d)) return 'domainSpaces'
  if (d.length > 253 || !labels.every(l => DOMAIN_LABEL_RE.test(l) && l.length <= 63)) return 'domainInvalid'
  if (d !== 'localhost' && labels.length < 2) return 'domainNoTld'
  if (existing.some(s => s.toLowerCase() === d)) return 'domainExists'
  return ''
}
