# NKS WebDev Console - Local Development Server Management

**NKS WebDev Console** is a unified local development environment for Windows, macOS, and Linux that replaces MAMP PRO, XAMPP, and WampServer. Manage Apache/Nginx, PHP versions, MySQL/MariaDB, and SSL—all from one intuitive interface.

## Why NKS WebDev Console?

- **One tool, one workflow** – No more juggling multiple server managers
- **Per-site PHP versions** – Run PHP 7.4 on one project, PHP 8.2 on another
- **Automatic SSL** – Self-signed certificates without manual configuration
- **Service plugins** – Extend with Redis, PostgreSQL, Elasticsearch, and more
- **Zero-config DNS** – `.local` and `.test` domains just work
- **Export/Import sites** – Move projects between machines and developers
- **Cross-platform** – Windows, macOS, Linux with identical workflows

## Key Features

- **Multi-Service Management**: Apache, Nginx, PHP-FPM, MySQL, MariaDB, memcached
- **PHP Version Management**: Install and switch between PHP 5.6–8.3
- **Per-Site Configuration**: Different PHP versions, extensions, and settings per project
- **SSL Certificates**: Auto-generate self-signed certificates, or import your own
- **Database Tools**: Create/export databases, phpMyAdmin integration, CLI access
- **CLI Tools**: Full command-line interface with JSON output for scripting
- **Framework Support**: Laravel, WordPress, Symfony, static site generators
- **Plugin Ecosystem**: Extensible architecture for services and frameworks
- **Dark/Light Themes**: Native GUI with system theme support

## Quick Start

### 1. Install NKS WebDev Console

**Windows:**
```bash
winget install nks-wdc
# or download from https://nks-wdc.sh/download
```

**macOS:**
```bash
brew tap nks-wdc/tap
brew install nks-wdc
```

**Linux (Ubuntu/Debian):**
```bash
sudo apt-get update
sudo apt-get install nks-wdc
```

### 2. Create Your First Site

```bash
wdc site create \
  --name my-project \
  --path /Users/you/projects/my-project \
  --php 8.2 \
  --domain my-project.local
```

Or use the GUI: **File → New Site** and follow the wizard.

### 3. Access Your Site

- Browser: `https://my-project.local`
- Database: `phpMyAdmin → http://localhost/phpmyadmin`
- CLI: `wdc database list`

All services start automatically. No configuration needed.

## Documentation

- **[Getting Started](./getting-started.md)** – Installation, first-run wizard, creating sites
- **[Sites Guide](./guides/sites.md)** – Creating and managing virtual hosts
- **[PHP Management](./guides/php.md)** – Installing versions, extensions, configuration
- **[Database Guide](./guides/databases.md)** – MySQL, MariaDB, phpMyAdmin
- **[CLI Reference](./cli-reference.md)** – Complete command documentation
- **[Configuration](./configuration.md)** – Global and per-site settings
- **[Troubleshooting](./troubleshooting.md)** – Common issues and solutions
- **[Migration Guides](./migration.md)** – Move from MAMP PRO, XAMPP, WampServer, Laragon
- **[Plugin Development](./plugin-development.md)** – Extend NKS WebDev Console
- **[Architecture](./architecture.md)** – How NKS WebDev Console works internally

## System Requirements

### Windows
- Windows 10 or later (build 19041+)
- 2 GB RAM minimum (4 GB recommended)
- 500 MB disk space
- Administrator privileges for first install

### macOS
- macOS 11 (Big Sur) or later
- Intel or Apple Silicon support
- 2 GB RAM minimum (4 GB recommended)
- Administrator privileges for first install

### Linux
- Ubuntu 20.04 LTS or later, Fedora 32+, or Debian 10+
- 2 GB RAM minimum (4 GB recommended)
- `sudo` access for service management

## GUI vs CLI

**GUI** (recommended for beginners):
- Start/stop services with one click
- Create sites with a wizard
- Visualize port usage and service status
- Edit configuration files in built-in editor

**CLI** (recommended for automation):
- Scripting and automation
- CI/CD integration
- Remote server management
- JSON output for parsing

Both are fully featured. Use whichever fits your workflow.

## Getting Help

- **Documentation**: https://docs.nks-wdc.sh
- **Discord Community**: https://discord.gg/nks-wdc
- **GitHub Issues**: https://github.com/nks-hub/webdev-console/issues
- **Email Support**: support@nks-wdc.sh (Pro tier)

## License

NKS WebDev Console is open-source under the MIT License. Premium features (Pro tier) require a license key.

## Version

Current version: **2.0.0** | Last updated: 2026-04-09

---

**Ready to start?** → [Getting Started Guide](./getting-started.md)
