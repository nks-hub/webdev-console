<template>
  <div class="help-page">
    <div class="page-header">
      <h1 class="page-title">Help</h1>
    </div>

    <div class="page-body help-body">
      <aside class="help-sidebar">
        <div class="help-search">
          <el-input v-model="searchQuery" size="small" placeholder="Search help…" clearable>
            <template #prefix><el-icon><Search /></el-icon></template>
          </el-input>
        </div>
        <nav class="help-nav">
          <button
            v-for="section in filteredSections"
            :key="section.id"
            class="help-nav-item"
            :class="{ active: activeSection === section.id }"
            @click="activeSection = section.id"
          >
            <span class="help-nav-icon"><el-icon><component :is="section.icon" /></el-icon></span>
            {{ section.title }}
          </button>
        </nav>
      </aside>

      <article class="help-content">
        <section v-for="section in filteredSections" :key="section.id" v-show="activeSection === section.id">
          <h2 class="help-section-title">{{ section.title }}</h2>
          <div class="help-prose" v-html="section.html" />
        </section>
        <div v-if="filteredSections.length === 0" class="help-empty">
          No help sections match &ldquo;{{ searchQuery }}&rdquo;.
        </div>
      </article>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, type Component } from 'vue'
import {
  Search, House, Monitor, DataLine, Lock, Connection, Document,
  Setting, Box, Download, DataBoard, InfoFilled, Tools, Histogram,
} from '@element-plus/icons-vue'

interface HelpSection {
  id: string
  title: string
  icon: Component
  body: string
}

// F89: per-section Help content. Kept inline (not fetched markdown) so
// the help surface works offline and survives daemon outages — nothing
// here should require a network round-trip. v-html is rendered from a
// static string constant so there's no XSS surface.
const sections: HelpSection[] = [
  {
    id: 'overview',
    title: 'Overview',
    icon: House,
    body: `
<p>NKS WebDev Console is a local development environment manager for Windows, macOS and Linux.
It replaces MAMP / XAMPP / Laragon with a unified interface for Apache, Nginx, PHP, MySQL,
MariaDB, Redis, Mailpit, Composer, SSL (mkcert) and Cloudflare Tunnels.</p>
<p>Everything runs as a local daemon process; the Electron app is just the control surface.
You can switch UI modes between <strong>Simple</strong> (one-page sites) and
<strong>Advanced</strong> (full per-service management) in the header.</p>
<h3>Architecture</h3>
<ul>
  <li><a href="https://github.com/nks-hub/webdev-console" target="_blank">webdev-console</a> — this app (Electron + Vue 3 + .NET 9 daemon)</li>
  <li><a href="https://github.com/nks-hub/webdev-console-plugins" target="_blank">webdev-console-plugins</a> — service plugins (Apache, MySQL, PHP, …)</li>
  <li><a href="https://github.com/nks-hub/wdc-catalog-api" target="_blank">wdc-catalog-api</a> — binary + plugin catalog backend</li>
  <li><a href="https://github.com/nks-hub/webdev-console-binaries" target="_blank">webdev-console-binaries</a> — prebuilt runtime binaries</li>
</ul>
`,
  },
  {
    id: 'sites',
    title: 'Sites',
    icon: Monitor,
    body: `
<p>Sites are the primary unit of work. Each site maps a local domain
(<code>example.loc</code>) to a document root (folder on disk) + a PHP version + optional
HTTPS + optional Cloudflare Tunnel.</p>
<h3>Simple Mode</h3>
<p>Minimal form on <code>/sites</code>: domain + document root (auto-suggested from
<code>C:\\work\\sites\\&lt;domain&gt;</code>), PHP version picker, Cloudflare Tunnel toggle, SSL toggle.
The Advanced-mode features (aliases, rewrite rules, custom headers) stay hidden.</p>
<h3>Advanced Mode</h3>
<p>Table view on <code>/sites</code>. Primary row action is <strong>Edit</strong> (opens SiteEdit
with full 9-tab configuration); Open-in-browser, Detect framework, Delete live in the row overflow menu.
Open routes through <code>shell.openExternal</code> so it launches your OS default browser, not a new
Electron window.</p>
<h3>SiteEdit tabs</h3>
<ul>
  <li><strong>General</strong> — domain, aliases, document root, PHP version</li>
  <li><strong>Runtime</strong> — PHP-FPM pool tuning, environment variables</li>
  <li><strong>Cloudflare</strong> — per-site tunnel config (zone, subdomain)</li>
  <li><strong>SSL</strong> — per-site cert switch + path overrides</li>
  <li><strong>Composer</strong> — composer.json package manager (see Composer section)</li>
  <li><strong>Metrics</strong> — request rate, historical traffic chart, access log</li>
  <li><strong>Errors</strong> — aggregated Apache + PHP-FPM + PHP-web error tail</li>
  <li><strong>History</strong> — config rollback points</li>
  <li><strong>Danger</strong> — rename domain / delete</li>
</ul>
`,
  },
  {
    id: 'databases',
    title: 'Databases',
    icon: DataBoard,
    body: `
<p>Manage MySQL / MariaDB databases at <code>/databases</code>. List, create, drop; run ad-hoc
SQL queries; import / export SQL dumps.</p>
<p>If the page shows <strong>"ERROR 1045 Access denied"</strong> the WDC-managed MySQL port (3306 by default)
is occupied by MAMP / XAMPP / a Windows service. Fix: Settings → Porty → change MySQL port, or stop the
external service.</p>
`,
  },
  {
    id: 'ssl',
    title: 'SSL certificates',
    icon: Lock,
    body: `
<p><code>/ssl</code> manages locally-trusted TLS certs via mkcert.</p>
<ul>
  <li><strong>Install CA</strong> — mkcert root into the system trust store. Runs once per machine.</li>
  <li><strong>Generate</strong> — issue a cert for a domain + optional aliases.</li>
  <li>Each cert card shows expiry countdown (badge turns amber &le; 14 days), issuer DN, SHA1 fingerprint.
  Orphan certs (no matching site) are dimmed with an <em>Orphan</em> badge.</li>
</ul>
`,
  },
  {
    id: 'composer',
    title: 'Composer',
    icon: Box,
    body: `
<p>Per-site Composer management lives on SiteEdit → Composer tab and globally on <code>/composer</code>.</p>
<ul>
  <li>Auto-discovers <code>composer.phar</code> under <code>~/.wdc/binaries/composer/&lt;version&gt;/</code>
      with semver-sorted selection.</li>
  <li>Finds a WDC-managed PHP binary automatically for the phar interpreter (falls back to system PHP).</li>
  <li>Install / Require / Remove / Diagnose / Outdated run through the plugin invoker with argument arrays
      (no shell interpolation — safe from command injection).</li>
  <li>Hover a package name to see the Packagist metadata popover (description, downloads, repo link).</li>
  <li>Columns are sortable. Outdated packages are marked with an amber dot.</li>
</ul>
`,
  },
  {
    id: 'cloudflare',
    title: 'Cloudflare Tunnel',
    icon: Connection,
    body: `
<p><code>/cloudflare</code> exposes WDC sites to a public <code>&lt;subdomain&gt;.&lt;zone&gt;</code> URL
via Cloudflare Tunnel, so external collaborators can preview your local dev without opening firewall
ports.</p>
<p>Set up once: paste your Cloudflare API token + pick a zone. Per-site: enable the Tunnel switch on
SiteEdit → Cloudflare (or the Simple-mode toggle during site creation) and the daemon creates the DNS
record and cloudflared ingress rule for you.</p>
<p><strong>Note</strong>: <code>cloudflared</code> is a single process routing ALL tunneled sites.
Restarting it affects the entire tunnel fleet.</p>
`,
  },
  {
    id: 'hosts',
    title: 'Hosts file',
    icon: Document,
    body: `
<p><code>/hosts</code> is a safer editor for the system hosts file. WDC-managed entries live inside a
<code>BEGIN NKS WebDev Console</code> / <code>END NKS WebDev Console</code> marker block — anything
outside that block is preserved verbatim.</p>
<p>Requires administrator privileges on Windows. The Apply button automatically creates a timestamped
<code>.bak</code> in <code>~/.wdc/backups/hosts/</code> before each write, and Restore accepts a <code>.bak</code>
upload clamped to 10 MiB.</p>
`,
  },
  {
    id: 'plugins',
    title: 'Plugins',
    icon: Tools,
    body: `
<p>WDC is plugin-driven. The daemon loads <code>IWdcPlugin</code> assemblies from the plugin dir (one
per service: Apache, MySQL, PHP, Redis, Composer, SSL, Cloudflare, …). Each plugin is independently
versioned and distributed through GitHub releases + wdc-catalog-api.</p>
<p>Writing a plugin: see <a href="https://github.com/nks-hub/webdev-console-plugins/blob/main/docs/PLUGIN-API.md" target="_blank">PLUGIN-API.md</a>
and the <a href="https://github.com/nks-hub/webdev-console-plugins/blob/main/docs/WRITING-A-PLUGIN.md" target="_blank">WRITING-A-PLUGIN walkthrough</a>.</p>
`,
  },
  {
    id: 'mcp',
    title: 'MCP dev API',
    icon: Histogram,
    body: `
<p>WDC ships an MCP (Model Context Protocol) server so AI dev tools (Claude Code, Cursor, etc.) can
automate site creation / management. It's sandboxed to the daemon's authenticated API surface — no
direct filesystem access.</p>
<p>The MCP server runs as a separate service in the daemon process. Point your MCP-compatible tool at
the nks-wdc transport to expose tools like <code>wdc_create_site</code>, <code>wdc_list_databases</code>,
<code>wdc_generate_cert</code>, <code>wdc_cloudflare_config</code>, and more.</p>
`,
  },
  {
    id: 'shortcuts',
    title: 'Keyboard shortcuts',
    icon: InfoFilled,
    body: `
<ul>
  <li><code>Ctrl+K</code> / <code>Cmd+K</code> — open Command Palette</li>
  <li><code>F5</code> — refresh daemon state</li>
  <li><code>Ctrl+N</code> — new site</li>
  <li><code>Ctrl+1..7</code> — jump to Dashboard / Sites / Databases / SSL / PHP / Binaries / Settings</li>
</ul>
`,
  },
  {
    id: 'settings',
    title: 'Settings',
    icon: Setting,
    body: `
<p><code>/settings</code> covers:</p>
<ul>
  <li><strong>General</strong> — language, theme (dark / light / system), UI mode (Simple / Advanced),
      run on startup, auto-start services, default PHP version, DNS cache flush, MAMP PRO import,
      telemetry + crash reports.</li>
  <li><strong>Porty</strong> — override ports for each managed service (MySQL, Apache, Redis, …) when
      you need to avoid collisions with other tools.</li>
  <li><strong>Cesty</strong> — override binary paths + the system hosts file path; Browse buttons open
      the native folder / file picker.</li>
  <li><strong>Databáze</strong> — default MySQL admin credentials the daemon uses when WDC owns the
      server.</li>
  <li><strong>Pokročilé</strong> — plugin toggles, service auto-restart, catalog URL override.</li>
  <li><strong>Účet</strong> — optional sync: push / pull your WDC config snapshot to
      <a href="https://wdc.nks-hub.cz" target="_blank">wdc-catalog-api</a> so a fresh install hydrates
      from the last known good backup.</li>
  <li><strong>Aktualizace</strong> — check for new WDC releases; the header also surfaces an
      update-available badge automatically when a newer tag is published on GitHub.</li>
  <li><strong>About</strong> — version, sources, runtime info (daemon uptime, PID, version).</li>
</ul>
`,
  },
  {
    id: 'binaries',
    title: 'Binaries',
    icon: Download,
    body: `
<p><code>/binaries</code> manages runtime binaries (Apache, Nginx, PHP, MySQL, MariaDB, Redis, Caddy,
Mailpit, cloudflared, mkcert, Node, Composer). Each one is downloaded on-demand from the
<a href="https://github.com/nks-hub/webdev-console-binaries" target="_blank">webdev-console-binaries</a>
GitHub release matching your OS + arch. SHA-256 is verified before unpacking.</p>
<p>The list shows installed versions + latest available. Upgrade in-place keeps older versions on
disk as a rollback target.</p>
`,
  },
  {
    id: 'metrics',
    title: 'Metrics + logs',
    icon: DataLine,
    body: `
<p>Each site has per-request metrics derived from the Apache access log, and aggregated error logs
pulled from Apache + PHP-FPM + PHP-web. See SiteEdit → Metrics + Errors.</p>
<p>Dashboard aggregates across all running services: CPU, RAM, request-per-minute sparkline, error
count badge. Click a tile to drill down into that specific surface.</p>
`,
  },
]

const activeSection = ref<string>('overview')
const searchQuery = ref<string>('')

const processed = sections.map((s) => ({
  ...s,
  html: s.body,
  searchText: `${s.title} ${s.body}`.toLowerCase(),
}))

const filteredSections = computed(() => {
  const q = searchQuery.value.trim().toLowerCase()
  if (!q) return processed
  return processed.filter((s) => s.searchText.includes(q))
})
</script>

<style scoped>
.help-page { display: flex; flex-direction: column; height: 100%; }
.help-body { display: flex; gap: 24px; flex: 1; min-height: 0; }
.help-sidebar {
  width: 220px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding-right: 8px;
  border-right: 1px solid var(--wdc-border);
}
.help-search { padding: 0 4px; }
.help-nav { display: flex; flex-direction: column; gap: 2px; }
.help-nav-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  color: var(--wdc-text-2);
  font-size: 0.84rem;
  text-align: left;
  cursor: pointer;
  transition: background 0.15s ease;
}
.help-nav-item:hover { background: var(--wdc-surface-2); color: var(--wdc-text); }
.help-nav-item.active {
  background: rgba(86, 194, 255, 0.08);
  border-color: var(--wdc-border);
  color: var(--wdc-accent);
  font-weight: 600;
}
.help-nav-icon { display: inline-flex; }

.help-content {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 0 12px 40px;
}
.help-section-title {
  font-size: 1.4rem;
  font-weight: 700;
  margin: 0 0 16px;
  color: var(--wdc-text);
}
.help-prose {
  font-size: 0.92rem;
  line-height: 1.6;
  color: var(--wdc-text);
  max-width: 720px;
}
.help-prose :deep(h3) { margin-top: 20px; font-size: 1rem; color: var(--wdc-text); }
.help-prose :deep(code) {
  background: var(--wdc-surface-2);
  padding: 2px 6px;
  border-radius: 4px;
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
  color: var(--wdc-accent);
}
.help-prose :deep(a) { color: var(--wdc-accent); text-decoration: none; }
.help-prose :deep(a:hover) { text-decoration: underline; }
.help-prose :deep(ul) { padding-left: 20px; margin: 8px 0; }
.help-prose :deep(li) { margin-bottom: 4px; }

.help-empty {
  padding: 40px 12px;
  color: var(--wdc-text-3);
  font-style: italic;
}
</style>
