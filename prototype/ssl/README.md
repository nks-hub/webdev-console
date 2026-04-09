# DevForge SSL Certificate Manager

Automated SSL certificate lifecycle management for local development domains using [mkcert](https://github.com/nickg/mkcert).

## Overview

DevForge SSL Manager generates locally-trusted SSL certificates for `*.test` domains (or any local development domain) so that HTTPS works without browser warnings during development.

It wraps `mkcert` with a structured storage layer, metadata tracking, and Apache configuration generation.

## Architecture

```
~/.devforge/ssl/
  ├── sites/
  │   ├── myapp.test/
  │   │   ├── cert.pem          # X.509 certificate
  │   │   └── key.pem           # Private key
  │   └── another.test/
  │       ├── cert.pem
  │       └── key.pem
  ├── metadata/                  # JSON metadata (PowerShell)
  │   ├── myapp.test.json
  │   └── another.test.json
  └── certs.db                   # SQLite metadata (Go)
```

## Prerequisites

- **mkcert** installed at `C:\work\mkcert.exe` (or on PATH)
- **OpenSSL** available (Git for Windows ships one, or use MAMP's)
- **MAMP** (optional) for Apache integration at `C:\MAMP\`

## Components

### ssl_manager.ps1 (PowerShell)

Full-featured CLI for certificate management.

```powershell
# Install mkcert CA in Windows trust store (run as admin first time)
.\ssl_manager.ps1 -Action install-ca

# Generate a certificate with wildcard and www alias
.\ssl_manager.ps1 -Action generate -Domain "myapp.test" -Aliases "www.myapp.test","*.myapp.test"

# List all managed certificates with status and expiry
.\ssl_manager.ps1 -Action list

# Verify a certificate (expiry, key match, trust chain)
.\ssl_manager.ps1 -Action verify -Domain "myapp.test"

# Revoke (delete) a certificate
.\ssl_manager.ps1 -Action revoke -Domain "myapp.test"
```

### ssl_manager.go (Go)

Cross-platform implementation with SQLite metadata storage.

```bash
# Build
cd prototype/ssl
go mod tidy
go build -o ssl_manager.exe .

# Usage
ssl_manager.exe install-ca
ssl_manager.exe generate myapp.test www.myapp.test "*.myapp.test"
ssl_manager.exe list
ssl_manager.exe verify myapp.test
ssl_manager.exe revoke myapp.test
```

### apache_ssl.conf.tmpl

Apache vhost template with modern TLS settings. Placeholders:

| Placeholder         | Description                          |
|---------------------|--------------------------------------|
| `{{.Domain}}`       | Primary domain name                  |
| `{{.DocRoot}}`      | Document root path                   |
| `{{.CertPath}}`     | Path to cert.pem                     |
| `{{.KeyPath}}`      | Path to key.pem                      |
| `{{.ServerAliases}}`| Space-separated alias domains        |

Security features in the template:
- TLS 1.2+ only (SSLv3, TLSv1, TLSv1.1 disabled)
- Mozilla Intermediate cipher suite
- HSTS with `max-age=0` (safe for local dev, does not pin)
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: SAMEORIGIN`

### test_ssl.ps1

Integration test that exercises the full workflow:

```powershell
.\test_ssl.ps1
```

Steps:
1. Checks preconditions (mkcert, openssl, scripts)
2. Generates a cert for `devforge-test.test`
3. Creates an Apache vhost config from the template
4. Verifies the cert with OpenSSL (parsing, expiry, key match)
5. Displays cert details (subject, issuer, SANs, fingerprint)
6. Runs `list` and `verify` actions
7. Cleans up via `revoke`

## How It Works

### Certificate Generation Flow

1. **CA Installation** (one-time): `mkcert -install` adds a locally-generated root CA to the Windows certificate store. All certs signed by this CA are trusted by browsers.

2. **Certificate Generation**: `mkcert -cert-file <path> -key-file <path> domain [aliases...]` creates a PEM certificate and private key signed by the local CA.

3. **Metadata Storage**: After generation, the certificate is parsed with OpenSSL (PowerShell) or `crypto/x509` (Go) to extract Subject, Issuer, SANs, expiry, and SHA-256 fingerprint. This metadata is stored alongside the cert files.

4. **Apache Integration**: The template is filled with cert paths and domain info to produce a ready-to-use SSL vhost configuration.

### Security Considerations

- **Local CA only**: The root CA private key exists only on the developer's machine (`mkcert -CAROOT`). It never leaves the machine.
- **No wildcard trust**: Each cert is scoped to specific domains/SANs.
- **HSTS max-age=0**: Prevents browsers from caching HSTS pins for `.test` domains, which would break HTTP-only sites on the same machine.
- **Modern TLS only**: Template disables SSLv3, TLSv1.0, TLSv1.1.
- **Cert verification**: The `verify` action checks expiry, key-cert match, and trust chain back to the mkcert CA.

## Integration with DevForge

The SSL manager is designed to be called by DevForge's site provisioning workflow:

```
devforge site create myapp.test
  -> ssl_manager generate myapp.test www.myapp.test *.myapp.test
  -> generate Apache vhost from template
  -> reload Apache
  -> site is accessible at https://myapp.test
```

## Troubleshooting

**"mkcert -install failed"**: Run PowerShell as Administrator. mkcert needs elevated privileges to install the root CA in the Windows trust store.

**Browser still shows untrusted**: Close and reopen the browser after `install-ca`. Some browsers cache the trust store state.

**Certificate expired**: mkcert generates certs valid for ~2 years. Use `list` to check expiry, then `revoke` + `generate` to renew.

**Key does not match certificate**: Delete the domain directory and regenerate. This can happen if cert/key files were edited or mixed up.
