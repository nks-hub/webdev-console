# Troubleshooting

The first thing to try for almost any issue is `wdc doctor` — it runs 13
checks (port collisions, DNS, daemon connectivity, missing extensions, file
permissions, mkcert CA install, MySQL root password, services running, etc.)
and tells you what's broken before you have to guess.

## Daemon doesn't start / GUI shows "Daemon offline"

1. Check the port file — daemon writes its port + Bearer token here:
   - Windows: `%TEMP%\nks-wdc-daemon.port`
   - macOS: `$TMPDIR/nks-wdc-daemon.port`
   - Linux: `/tmp/nks-wdc-daemon.port`
2. If the file is missing, the daemon never started. Check:
   - `~/.wdc/logs/daemon.log` for stack traces
   - Port collision on 5000 (`lsof -i :5000` on macOS/Linux,
     `netstat -ano | findstr :5000` on Windows)
3. If the file exists but the GUI still says offline, the token may be stale
   — restart the app (the daemon writes a fresh token on every startup).

To run the daemon manually for debugging:

```bash
"/Applications/NKS WebDev Console.app/Contents/Resources/daemon/NKS.WebDevConsole.Daemon"
# or from source:
dotnet run --project src/daemon/NKS.WebDevConsole.Daemon
```

It logs everything to stderr and to `~/.wdc/logs/daemon.log`.

## Site shows "Site can't be reached"

Three usual suspects:

1. **DNS not resolved** — `.local`/`.test`/`.localhost` should resolve to
   `127.0.0.1`. The Hosts plugin manages `/etc/hosts`
   (`%WINDIR%\System32\drivers\etc\hosts` on Windows). Re-run
   `wdc dns sync` to rewrite it; verify with `ping my-site.local`.
2. **Apache not running** — `wdc service status apache`. If stopped,
   `wdc service start apache`. If start fails, check
   `~/.wdc/binaries/apache/<ver>/logs/error.log`.
3. **Port collision** — Apache binds 80/443 by default. MAMP, XAMPP, IIS,
   Docker port mappings can hold those. Stop them or change NKS WDC's
   ports in **Settings → Network**.

## SSL warning in browser

Expected on first launch — the per-site cert is signed by the local mkcert
CA, which the browser doesn't trust until you install it.

```bash
mkcert -install                                 # one-time, all browsers
# or via NKS WDC:
wdc ssl install-ca
```

Restart the browser after install. Mobile devices accessing local sites
need the CA installed manually (export from `mkcert -CAROOT`).

## PHP error: "Call to undefined function …"

Missing extension. Check what's installed:

```bash
wdc php list                                    # versions
wdc php extensions <version>                    # extensions for that version
```

To enable an extension on Windows: edit `~/.wdc/sites-php/<domain>/php.ini`
(the per-site INI override) and uncomment the relevant `extension=` line, or
use the **Sites → PHP** panel in the GUI which writes the same file.
Restart php-cgi after the change.

On macOS/Linux PHP is typically built with most extensions baked in
(`php -m` lists them); if a missing extension is required, rebuild PHP with
the extension enabled or install via the package manager you used for PHP
itself.

## "Address already in use" on Apache start

Another process holds 80 or 443:

```bash
# macOS / Linux
sudo lsof -nP -iTCP -sTCP:LISTEN | grep -E ':(80|443) '

# Windows (PowerShell)
Get-NetTCPConnection -LocalPort 80,443 -State Listen
```

Stop the offender or change ports in Settings → Network. NKS WDC validates
the port is free before generating the vhost — if you bypass this, Apache
fails at start with `make_sock: could not bind`.

## MySQL won't start / "Access denied for user 'root'"

The root password is stored DPAPI-encrypted on Windows
(`~/.wdc/data/mysql-root.dpapi`), in macOS Keychain on macOS, in a
chmod-600 file on Linux. If it gets out of sync, the daemon can't connect.

Reset:

1. Stop MySQL (`wdc service stop mysql`).
2. Delete `~/.wdc/data/mysql-root.dpapi` (or the equivalent for your OS).
3. Start MySQL with `--skip-grant-tables`, set the new root password, restart
   normally.
4. The daemon re-prompts for the password on next start and writes it back
   to the secure store.

## `wdc` command not found

The `wdc` binary lives next to the daemon. The Windows installer adds it to
PATH; on macOS/Linux do it yourself:

```bash
# macOS (installed app)
echo 'export PATH="/Applications/NKS WebDev Console.app/Contents/Resources/daemon:$PATH"' \
  >> ~/.zshrc

# Linux (AppImage extracted)
export PATH="/path/to/extracted/resources/daemon:$PATH"
```

Or use the full path: `"/Applications/NKS WebDev Console.app/Contents/Resources/daemon/wdc" doctor`.

## File access slow on macOS

If a project sits on iCloud Drive, a Time Machine backup volume, or an
external drive, every file read is slow and PHP requests crawl. Move the
project to a local SSD path under `~/projects/` and re-create the site.

## Plugin fails to load: "FileNotFoundException: NKS.WebDevConsole.Plugin.SDK"

The SDK assembly didn't ship next to the daemon. With release builds this
shouldn't happen. From source make sure
`dotnet build WebDevConsole.sln -c Release` ran before the daemon — the
SDK is referenced explicitly so MSBuild copies it into the daemon's bin
folder.

If you wrote a custom plugin and hit this, double-check that any SDK types
your plugin uses are present in the daemon's
`PluginLoadContext.SharedAssemblies` list (otherwise the isolated ALC
re-loads them and you get type-identity mismatches).

## Catalog is empty or stale

The daemon downloads the binary catalog (PHP/Apache/MySQL versions and
download URLs) from a configured URL on startup.

1. **Settings → Advanced → Catalog URL** — must be reachable from your
   machine. Default is the bundled fallback (no network) which is conservative.
2. `wdc binaries catalog-url` shows the current URL; pass a new one to
   change it.
3. `wdc binaries catalog --refresh` (or `POST /api/binaries/catalog/refresh`)
   forces a re-fetch without restarting the daemon.

## Where to look for logs

| File                                                        | What's in it                                            |
| ----------------------------------------------------------- | ------------------------------------------------------- |
| `~/.wdc/logs/daemon.log`                                    | Daemon startup, plugin loading, REST request log        |
| `~/.wdc/logs/services/apache.log`                           | Apache plugin lifecycle (start/stop/reload)             |
| `~/.wdc/binaries/apache/<ver>/logs/error.log`               | Apache itself                                           |
| `~/.wdc/binaries/apache/<ver>/logs/<domain>-error.log`      | Per-vhost errors                                        |
| `~/.wdc/binaries/apache/<ver>/logs/<domain>-access.log`     | Per-vhost access (Combined Log Format)                  |
| `~/.wdc/data/mysql/error.log`                               | MySQL error log                                         |
| `~/.wdc/sites-php/<domain>/<ver>/logs/php_errors.log`       | Per-site PHP error log                                  |

`wdc logs tail apache` and `wdc logs tail <domain>` are convenience wrappers
around `tail -f` on these files.

## Reporting bugs

Open an issue at <https://github.com/nks-hub/webdev-console/issues> with:

- The output of `wdc doctor --json`
- The relevant log file excerpt (above table)
- OS + version + how you installed (release artifact vs build from source)
