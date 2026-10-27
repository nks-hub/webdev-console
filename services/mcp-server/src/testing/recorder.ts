// Test double for the daemon client: records every call the tools make and
// answers with whatever the test's responder returns (or throws).
//
// Usage in a test file:
//   vi.mock('../daemonClient.js', async (importOriginal) => ({
//     ...(await importOriginal<object>()),
//     daemonClient: (await import('../testing/recorder.js')).recorder.client,
//   }))

export interface RecordedCall {
  method: 'GET' | 'POST' | 'PUT' | 'DELETE'
  path: string
  body?: unknown
  headers?: Record<string, string>
  /** Set for non-JSON bodies sent with postText(). */
  contentType?: string
}

export type Responder = (call: RecordedCall) => unknown

class Recorder {
  calls: RecordedCall[] = []
  responder: Responder = () => ({})

  reset(responder: Responder = () => ({})): void {
    this.calls = []
    this.responder = responder
  }

  private async handle(call: RecordedCall): Promise<unknown> {
    this.calls.push(call)
    return this.responder(call)
  }

  readonly client = {
    get: (path: string, headers?: Record<string, string>) => this.handle({ method: 'GET', path, headers }),
    post: (path: string, body?: unknown, headers?: Record<string, string>) =>
      this.handle({ method: 'POST', path, body, headers }),
    postText: (path: string, text: string, contentType: string, headers?: Record<string, string>) =>
      this.handle({ method: 'POST', path, body: text, contentType, headers }),
    put: (path: string, body?: unknown, headers?: Record<string, string>) =>
      this.handle({ method: 'PUT', path, body, headers }),
    delete: (path: string, headers?: Record<string, string>) => this.handle({ method: 'DELETE', path, headers }),
    scopes: () => ['*'],
    hasScope: () => true,
  }
}

export const recorder = new Recorder()
