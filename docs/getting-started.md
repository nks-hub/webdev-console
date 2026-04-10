# Getting Started with NKS WebDev Console

This guide walks you through installing NKS WebDev Console and creating your first local development site.

## System Requirements

### Windows
- Windows 10 (build 19041) or Windows 11
- 2 GB RAM minimum, 4 GB recommended
- 500 MB free disk space
- Administrator access for initial setup
- No conflicts with existing MAMP PRO, XAMPP, or WampServer installations

### macOS
- macOS 11 Big Sur or later
- Intel or Apple Silicon (M1/M2/M3)
- 2 GB RAM minimum, 4 GB recommended
- Administrator access for initial setup
- Xcode Command Line Tools (auto-installed on first run)

### Linux
- Ubuntu 20.04 LTS, Debian 10+, Fedora 32+, or equivalent
- 2 GB RAM minimum, 4 GB recommended
- `sudo` privileges for service management
- systemd-based init system

## Installation

### Windows

**Option 1: Windows Package Manager (Recommended)**
```bash
winget install nks-wdc
```

**Option 2: Download Installer**
1. Visit https://nks-wdc.sh/download
2. Download `NKS WebDev Console-2.0.0-windows-x64.exe`
3. Run the installer
4. Click "Install" and accept Administrator privileges
5. Choose installation directory (default: `C:\Program Files\NKS WebDev Console`)

**Option 3: Portable Version**
Download `NKS WebDev Console-2.0.0-windows-portable.zip`, extract, and run `wdc.exe`.

### macOS

**Option 1: Homebrew (Recommended)**
```bash
brew tap nks-wdc/tap
brew install nks-wdc
```

**Option 2: Download DMG**
1. Visit https://nks-wdc.sh/download
2. Download `NKS WebDev Console-2.0.0.dmg`
3. Mount the image and drag NKS WebDev Console to Applications
4. Launch from Applications folder

**Option 3: Direct Download (M1/M2/M3)**
Download the ARM64 version from releases page for faster performance.

### Linux

**Ubuntu/Debian:**
```bash
sudo apt-get update
sudo apt-get install nks-wdc
```

**Fedora/RHEL:**
```bash
sudo dnf install nks-wdc
```

**Build from Source:**
```bash
git clone https://github.com/nks-hub/webdev-console.git
cd nks-wdc
./build.sh
sudo ./install.sh
```

## First-Run Wizard

When you launch NKS WebDev Console for the first time:

### Step 1: Welcome Screen
- Review the system requirements check
- Choose preferred port range (default: 8000–8010)
- Select HTTP/HTTPS preference

### Step 2: Locations
- **Data Directory**: Where NKS WebDev Console stores configurations, databases, and certificates
  - Windows: `C:\Users\{username}\AppData\Local\NKS WebDev Console`
  - macOS: `~/Library/Application Support/NKS WebDev Console`
  - Linux: `~/.local/share/nks-wdc`
- **Projects Directory**: Where your sites are stored (default: `~/projects`)

### Step 3: Database
- Choose MySQL or MariaDB (MariaDB recommended for new installations)
- Set root password (minimum 8 characters)
- Choose port (default: 3306)

### Step 4: PHP
- Select PHP version for default site (recommended: PHP 8.2)
- Choose default web server: Apache or Nginx (recommended: Nginx)
- Enable/disable common extensions (GD, Intl, PDO, etc.)

### Step 5: Services
- Toggle services to auto-start on boot: Apache/Nginx, PHP-FPM, MySQL/MariaDB
- Review memory limits (default: 256 MB each)
- Configure firewall rules (if on Windows with Windows Defender)

### Step 6: Review & Install
- Review all selections
- Click "Install" to create services
- Wait 2–3 minutes for first-run setup
- Click "Open Dashboard" to continue

## Creating Your First Site

### Via GUI

1. Open NKS WebDev Console
2. Click **File → New Site** or click the **+** button
3. Fill in the form:
   - **Site Name**: `my-project` (letters, numbers, hyphens only)
   - **Local Domain**: `my-project.local`
   - **Project Path**: `/Users/you/projects/my-project` (auto-created if doesn't exist)
   - **PHP Version**: 8.2 (can change per-site anytime)
   - **Web Server**: Nginx (default)
   - **Enable SSL**: Yes (recommended)
4. Click **Create Site**
5. Wait 10–15 seconds while NKS WebDev Console:
   - Creates web root directory
   - Generates SSL certificate
   - Creates virtual host configuration
   - Reloads web server

### Via CLI

```bash
wdc site create \
  --name my-project \
  --path /Users/you/projects/my-project \
  --php 8.2 \
  --domain my-project.local \
  --ssl true
```

## Accessing Your Site

### Browser Access
- **HTTPS**: `https://my-project.local`
- **HTTP**: `http://my-project.local` (auto-redirects to HTTPS if enabled)

**Note**: First visit shows a browser warning about self-signed certificate. Click "Advanced → Proceed" to continue. This is normal and safe for local development.

### SSH/Terminal Access
```bash
# SSH into site (macOS/Linux only)
nks-wdc ssh my-project

# This opens a shell in your project directory with proper PATH
```

### Database Access

**phpMyAdmin (Web UI):**
- Visit: `http://localhost/phpmyadmin`
- Username: `root`
- Password: (the one you set in the wizard)

**CLI Access:**
```bash
nks-wdc database connect
# or MySQL client directly:
mysql -u root -p -h 127.0.0.1
```

## Stopping Services

### GUI Method
1. Open NKS WebDev Console
2. Click the service in the sidebar
3. Click **Stop** button

### CLI Method
```bash
nks-wdc service stop nginx
nks-wdc service stop php-fpm
nks-wdc service stop mysql
```

### Stop All
```bash
nks-wdc service stop all
```

## Troubleshooting First-Run Issues

### Port Already in Use
**Symptom**: Installation fails with "Port 3306 already in use"

**Solution**:
1. Run: `wdc port-check`
2. If another service is on port 3306, either:
   - Stop the conflicting service
   - Change MySQL port in wizard to 3307 or higher
3. Restart the wizard: `wdc setup --force`

### DNS Not Resolving
**Symptom**: `my-project.local` shows "Site can't be reached"

**Solution**:
- **Windows**: Check Control Panel → Network → DNS settings. Should show `127.0.0.1` for local domains
- **macOS/Linux**: Run `wdc dns-sync` to update `/etc/hosts`
- Manual fix: Add to `/etc/hosts`:
  ```
  127.0.0.1    my-project.local
  ```

### PHP Extension Missing
**Symptom**: "Call to undefined function" in browser

**Solution**:
1. Check if extension is installed: `wdc php extensions`
2. Install missing extension: `wdc php install-extension gd`
3. Restart PHP: `wdc service restart php-fpm`

### Slow File Access (macOS)
**Symptom**: Page loads slowly even with simple sites

**Solution**: File access on networked drives is slow. Keep projects on local SSD, not external drives or cloud folders.

## Next Steps

- **[Creating & Managing Sites](./guides/sites.md)** – Advanced site configuration
- **[PHP Management](./guides/php.md)** – Install versions, configure extensions
- **[Database Guide](./guides/databases.md)** – Import data, manage databases
- **[CLI Reference](./cli-reference.md)** – Full command documentation

## Common First-Time Questions

**Q: Can I use MAMP PRO and NKS WebDev Console simultaneously?**
A: Not recommended. Both bind to port 80/443. Uninstall MAMP PRO first, or change NKS WebDev Console's port in Settings.

**Q: How do I move a site from MAMP PRO?**
A: See [Migrating from MAMP PRO](./migration.md#mamp-pro).

**Q: Can I have multiple PHP versions?**
A: Yes. Install additional versions via `wdc php install 7.4` and assign to specific sites.

**Q: Is my data backed up?**
A: NKS WebDev Console keeps configurations in the Data Directory. Databases are stored in the MySQL data folder. Regular backups recommended for production data.

**Q: Can I use this for production?**
A: No. NKS WebDev Console is for local development only. For production, use a proper server (DigitalOcean, AWS, etc.).

---

**Successfully installed?** Open NKS WebDev Console and create your first site using the GUI wizard above.
