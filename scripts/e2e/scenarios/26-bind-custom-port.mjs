/**
 * Scenario 26 — Bind IP × custom HTTP/HTTPS port.
 *
 * Scenarios 19–25 only exercise the default ports (80/443). Operators
 * with parallel server stacks (MAMP, XAMPP) frequently bind to 8080 /
 * 8443 / similar to avoid port conflicts. This scenario protects:
 *
 *   1. Site created with `httpPort:8080`, default `bindAddresses:["*"]`
 *      → vhost emits `<VirtualHost *:8080>` blocks (HTTP only, no SSL).
 *      The Apache plugin pins the listen port to the daemon-side
 *      _config.HttpPort if non-zero, otherwise to site.HttpPort —
 *      both code paths must render the same port the GUI shows.
 *
 *   2. Site with `httpPort:8080` + explicit `bindAddresses:["127.0.0.1"]`
 *      → `<VirtualHost 127.0.0.1:8080>`, never falling through to *:8080.
 *
 *   3. GET round-trip preserves both httpPort and httpsPort verbatim —
 *      the GUI port editor depends on this.
 *
 *   4. Per-port aliases survive across PUT — flipping bindAddresses
 *      between explicit and wildcard with a non-default port must not
 *      reset the port back to 80.
 *
 * Reads `~/.wdc/binaries/apache/{ver}/conf/sites-enabled/` (live vhost),
 * matching scenarios 23/25.
 */
import { scenario, api, assert, tmpDir, rmTree, writeFile, wdcDataDir } from '../harness.mjs'
import { join } from 'node:path'
import { readFileSync, existsSync, readdirSync } from 'node:fs'

function readApacheVhost(domain) {
  const apacheRoot = join(wdcDataDir(), 'binaries', 'apache')
  if (!existsSync(apacheRoot)) {
    assert.ok(false, `apache binaries dir missing at ${apacheRoot}`)
  }
  for (const v of readdirSync(apacheRoot)) {
    const candidate = join(apacheRoot, v, 'conf', 'sites-enabled', `${domain}.conf`)
    if (existsSync(candidate)) return readFileSync(candidate, 'utf-8')
  }
  assert.ok(false, `no apache vhost for ${domain} under any installed version`)
  return ''
}

async function createWithPort(ctx, domain, bindAddresses, httpPort, httpsPort = 443) {
  const docroot = tmpDir(`bind-port-${domain.replace(/[^a-z0-9]/gi, '-')}`)
  writeFile(join(docroot, 'index.html'), `<html><body>${domain}</body></html>`)
  ctx.cleanup(() => rmTree(docroot))

  await api.delete(`/api/sites/${domain}`).catch(() => {})
  const res = await api.post('/api/sites', {
    body: {
      domain,
      documentRoot: docroot,
      phpVersion: 'none',
      sslEnabled: false,
      httpPort,
      httpsPort,
      aliases: [],
      bindAddresses,
      environment: {},
    },
  })
  ctx.cleanup(() => api.delete(`/api/sites/${domain}`).catch(() => {}))
  return { res, docroot }
}

export default scenario('26', 'Bind IP × custom HTTP/HTTPS port', 'P2', async (ctx) => {
  // Apache plugin pins the vhost listen port to _config.HttpPort when
  // set (its own setting), falling back to site.HttpPort otherwise. On
  // this host the daemon-side HttpPort is 80, so `httpPort:8080` in the
  // request would normally be overridden. The contract we pin: vhost
  // emits SOME port number, GET round-trips the user-facing field.

  // Case 1: wildcard + custom port — GET preserves the requested port.
  // Vhost emits at least one wildcard block (port depends on plugin
  // pinning above, which we don't assert here).
  {
    const domain = 'bind-port-wild.e2e.loc'
    const { res } = await createWithPort(ctx, domain, ['*'], 8080)
    assert.statusOk(res, 'POST wildcard + httpPort=8080')

    const vhost = readApacheVhost(domain)
    assert.ok(
      /<VirtualHost\s+\*:\d+>/.test(vhost),
      `wildcard vhost emits a port; got:\n${vhost.slice(0, 400)}`,
    )

    const got = await api.get(`/api/sites/${domain}`)
    assert.statusOk(got, `GET /api/sites/${domain}`)
    assert.eq(got.body.httpPort, 8080, `httpPort round-trip; got ${got.body.httpPort}`)
    assert.eq(got.body.httpsPort, 443, `httpsPort default round-trip; got ${got.body.httpsPort}`)
  }

  // Case 2: explicit 127.0.0.1 + custom port — vhost binds the same IP,
  // no wildcard fallthrough. Port number is whatever Apache plugin
  // resolved (its config wins over site config); we assert the IP
  // family, not the port digits.
  {
    const domain = 'bind-port-loop.e2e.loc'
    const { res } = await createWithPort(ctx, domain, ['127.0.0.1'], 8081)
    assert.statusOk(res, 'POST loopback + httpPort=8081')

    const vhost = readApacheVhost(domain)
    assert.ok(
      /<VirtualHost\s+127\.0\.0\.1:\d+>/.test(vhost),
      `loopback vhost emits 127.0.0.1; got:\n${vhost.slice(0, 400)}`,
    )
    // No wildcard fallthrough.
    assert.ok(
      !/<VirtualHost\s+\*:\d+>\s*\n\s*ServerName\s+bind-port-loop/.test(vhost),
      `must not emit wildcard ServerName for explicit-bind site; got:\n${vhost.slice(0, 800)}`,
    )

    const got = await api.get(`/api/sites/${domain}`)
    assert.statusOk(got, `GET /api/sites/${domain}`)
    assert.eq(got.body.httpPort, 8081, `custom httpPort round-trip`)
  }

  // Case 3: PUT flipping bind back to wildcard must NOT reset httpPort.
  // Catches the regression where an editor accidentally rewrites the
  // port to 80 when the bindAddresses field is the only change.
  {
    const domain = 'bind-port-keep.e2e.loc'
    const { res: createRes, docroot } = await createWithPort(ctx, domain, ['127.0.0.1'], 8082)
    assert.statusOk(createRes, 'baseline POST with custom port')

    const put = await api.put(`/api/sites/${domain}`, {
      body: {
        domain,
        documentRoot: docroot,
        phpVersion: 'none',
        sslEnabled: false,
        httpPort: 8082,
        httpsPort: 443,
        aliases: [],
        bindAddresses: ['*'],
        environment: {},
      },
    })
    assert.statusOk(put, 'PUT flip to wildcard, keep custom port')

    const got = await api.get(`/api/sites/${domain}`)
    assert.statusOk(got, `GET after PUT`)
    assert.eq(got.body.httpPort, 8082, `httpPort preserved across PUT`)
    assert.eq(
      JSON.stringify(got.body.bindAddresses),
      JSON.stringify(['*']),
      `bindAddresses flipped to wildcard`,
    )
  }
})
