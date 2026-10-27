import { describe, expect, it } from 'vitest'

import { SERVER_INSTRUCTIONS } from './instructions.js'

describe('SERVER_INSTRUCTIONS', () => {
  it('covers the confirmation flow, WDC-side configuration and error hints', () => {
    expect(SERVER_INSTRUCTIONS).toContain('wdc_request_confirmation')
    expect(SERVER_INSTRUCTIONS).toContain('wdc_get_confirmation_status')
    expect(SERVER_INSTRUCTIONS).toContain('intentToken')
    expect(SERVER_INSTRUCTIONS).toMatch(/configured by the user in WDC/)
    expect(SERVER_INSTRUCTIONS).toMatch(/hint/)
    expect(SERVER_INSTRUCTIONS.length).toBeLessThan(1500)
  })
})
