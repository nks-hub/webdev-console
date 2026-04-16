#!/usr/bin/env bash
#
# Bootstraps the host binaries NKS WebDev Console expects on macOS arm64.
# Installs Apache (httpd), PHP, MariaDB and mkcert via Homebrew, then symlinks
# them into ~/.wdc/binaries/<svc>/<ver>/bin/ so the daemon's plugins find
# them at the layout `<service>/<version>/bin/<binary>` they expect.
#
# Idempotent: re-running picks up version drifts and refreshes symlinks.
# Brew packages already installed are skipped.
#
# After this finishes, launch the app — Apache + PHP-FPM + MariaDB will all
# come up automatically.
#
# Linux equivalent is on the roadmap (apt/dnf bootstrap).

set -euo pipefail

WDC="${WDC_DATA_DIR:-$HOME/.wdc}"
BIN="$WDC/binaries"

if ! command -v brew >/dev/null 2>&1; then
  echo "Homebrew is required. Install from https://brew.sh first." >&2
  exit 1
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This script is macOS-only. See README for Linux/Windows install paths." >&2
  exit 1
fi

echo "==> Installing brew packages (httpd, php, mariadb, mkcert, caddy, mailpit, cloudflared, redis)..."
brew install httpd php mariadb mkcert caddy mailpit cloudflare/cloudflare/cloudflared redis >/dev/null

# Detect installed versions so symlink directories match.
HTTPD_VER=$(/opt/homebrew/opt/httpd/bin/httpd -v | awk '/Server version/ { split($3, a, "/"); print a[2] }')
PHP_VER=$(/opt/homebrew/opt/php/bin/php -r 'echo PHP_VERSION;')
PHP_MM=$(echo "$PHP_VER" | cut -d. -f1-2)
MARIA_VER=$(/opt/homebrew/opt/mariadb/bin/mysqld --version | awk '{ for (i=1;i<=NF;i++) if ($i ~ /^[0-9]+\.[0-9]+\.[0-9]+/) { print $i; exit } }' | tr -d ',')

echo "==> Detected: httpd=$HTTPD_VER  php=$PHP_VER  mariadb=$MARIA_VER  mkcert=$(mkcert -version 2>&1 | head -1)"

mkdir -p \
  "$BIN/apache/$HTTPD_VER/bin" \
  "$BIN/apache/$HTTPD_VER/conf/sites-enabled" \
  "$BIN/apache/$HTTPD_VER/logs" \
  "$BIN/apache/$HTTPD_VER/modules" \
  "$BIN/mariadb/$MARIA_VER/bin" \
  "$BIN/mariadb/$MARIA_VER/share" \
  "$BIN/mysql" \
  "$BIN/php/$PHP_VER"

# --- Apache layout (httpd binary + bundled modules + brew-shipped config files) ---
ln -sfn "/opt/homebrew/opt/httpd/bin/httpd"            "$BIN/apache/$HTTPD_VER/bin/httpd"
ln -sfn "/opt/homebrew/etc/httpd/mime.types"           "$BIN/apache/$HTTPD_VER/conf/mime.types"
ln -sfn "/opt/homebrew/etc/httpd/magic"                "$BIN/apache/$HTTPD_VER/conf/magic"
ln -sfn "/opt/homebrew/etc/httpd/extra"                "$BIN/apache/$HTTPD_VER/conf/extra"
ln -sfn "/opt/homebrew/share/httpd/error"              "$BIN/apache/$HTTPD_VER/error"
ln -sfn "/opt/homebrew/share/httpd/icons"              "$BIN/apache/$HTTPD_VER/icons"
# Modules (mod_proxy_fcgi, mod_rewrite, etc.) — symlink each .so file
for mod in /opt/homebrew/opt/httpd/lib/httpd/modules/*.so; do
  ln -sfn "$mod" "$BIN/apache/$HTTPD_VER/modules/$(basename "$mod")"
done
# Default DocumentRoot — empty htdocs prevents AH00526 on plain Apache start
mkdir -p "$BIN/apache/$HTTPD_VER/htdocs"
[[ -f "$BIN/apache/$HTTPD_VER/htdocs/index.html" ]] || \
  echo '<h1>NKS WDC Apache OK</h1>' > "$BIN/apache/$HTTPD_VER/htdocs/index.html"

# --- MariaDB layout (mysqld + helpers + share for english language files) ---
for tool in mysqld mysqladmin mysql mariadb-install-db; do
  ln -sfn "/opt/homebrew/opt/mariadb/bin/$tool" "$BIN/mariadb/$MARIA_VER/bin/$tool"
done
ln -sfn "/opt/homebrew/opt/mariadb/share/mariadb" "$BIN/mariadb/$MARIA_VER/share/mariadb"
# Plugin looks for binaries under binaries/mysql/ — alias the mariadb tree there
ln -sfn "$BIN/mariadb/$MARIA_VER" "$BIN/mysql/$MARIA_VER"

# --- PHP layout (php + php-fpm + extension dir) ---
ln -sfn "/opt/homebrew/opt/php/bin/php"      "$BIN/php/$PHP_VER/php"
ln -sfn "/opt/homebrew/opt/php/sbin/php-fpm" "$BIN/php/$PHP_VER/php-fpm"
ln -sfn "/opt/homebrew/opt/php/lib/php"      "$BIN/php/$PHP_VER/ext"

# --- Single-binary services (Mailpit/Caddy/Redis/Cloudflared) ---
# Plugins look at <BinariesRoot>/<svc>/<ver>/<binary> directly (top-level),
# not under bin/ — so the file goes one level above the bin/ pattern.
CADDY_VER=$(/opt/homebrew/opt/caddy/bin/caddy version 2>&1 | head -1 | awk '{print $1}' | sed 's/^v//')
MAIL_VER=$(/opt/homebrew/opt/mailpit/bin/mailpit version 2>&1 | head -1 | awk '{print $2}' | sed 's/^v//')
CLOUD_VER=$(cloudflared --version 2>&1 | head -1 | awk '{print $3}')
REDIS_VER=$(/opt/homebrew/opt/redis/bin/redis-server --version 2>&1 | grep -oE 'v=[^ ]+' | sed 's/v=//')

mkdir -p "$BIN/caddy/$CADDY_VER" "$BIN/mailpit/$MAIL_VER" "$BIN/cloudflared/$CLOUD_VER" "$BIN/redis/$REDIS_VER"
ln -sfn "/opt/homebrew/opt/caddy/bin/caddy"        "$BIN/caddy/$CADDY_VER/caddy"
ln -sfn "/opt/homebrew/opt/mailpit/bin/mailpit"    "$BIN/mailpit/$MAIL_VER/mailpit"
ln -sfn "/opt/homebrew/bin/cloudflared"            "$BIN/cloudflared/$CLOUD_VER/cloudflared"
ln -sfn "/opt/homebrew/opt/redis/bin/redis-server" "$BIN/redis/$REDIS_VER/redis-server"

echo "==> Layout ready under $BIN"
echo "    apache:  $BIN/apache/$HTTPD_VER"
echo "    mariadb: $BIN/mariadb/$MARIA_VER  (alias: $BIN/mysql/$MARIA_VER)"
echo "    php:     $BIN/php/$PHP_VER"
echo
echo "==> mkcert local CA — installing into system trust store (may prompt for sudo)"
mkcert -install || echo "    (skip if already installed)"
echo
echo "==> All set. Launch /Applications/NKS\\ WebDev\\ Console.app and Apache + PHP-FPM + MariaDB"
echo "    will come up automatically. Default ports: Apache 8080, MariaDB 3306."
echo "    To bind privileged ports (80/443), pass HttpPort/HttpsPort in Settings."
