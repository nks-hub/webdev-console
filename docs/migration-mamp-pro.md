# Migrating from MAMP PRO to NKS WebDev Console

This guide helps you move your projects, databases, and configurations from MAMP PRO to NKS WebDev Console with minimal downtime.

**Estimated migration time**: 30 minutes for typical setup with 5–10 projects

## Why Migrate from MAMP PRO?

- **Simpler interface** – NKS WebDev Console focuses on essential features
- **Per-site PHP versions** – No need to restart to switch versions
- **Better performance** – Nginx option, no bloated UI overhead
- **Automatic SSL** – Self-signed certificates included
- **Open source** – Free, extensible, active community
- **Cross-platform** – Same workflow on Windows, macOS, Linux

## Pre-Migration Checklist

Before starting, prepare your system:

```bash
# 1. Backup MAMP PRO databases
/Applications/MAMP/Library/bin/mysqldump -u root -proot --all-databases > ~/mamp-backup.sql

# 2. List running sites (remember URLs and PHP versions)
# Check MAMP PRO preferences

# 3. Note any custom Apache/PHP configuration
# MAMP PRO location: /Applications/MAMP/conf/

# 4. Back up project files (optional but recommended)
cp -r ~/Sites ~/Sites-backup
```

## Migration Steps

### Step 1: Install NKS WebDev Console

**macOS**:
```bash
brew tap nks-wdc/tap
brew install nks-wdc
```

**Windows**:
```bash
winget install nks-wdc
# or download from https://nks-wdc.sh/download
```

### Step 2: Run First-Time Wizard

Launch NKS WebDev Console. The wizard will:
1. Check system requirements
2. Choose ports for services (recommend different ports than MAMP PRO to avoid conflicts)
3. Set MySQL root password
4. Select default PHP version

**Important**: Use a different MySQL port (e.g., 3307) if keeping MAMP PRO temporarily:
- Wizard → Database → Port: 3307
- Then update your `.env` files accordingly

### Step 3: Import Your Databases

#### Option A: Quick Import (Recommended)
```bash
# Restore your MAMP PRO backup to NKS WebDev Console
nks-wdc database import ~/mamp-backup.sql
```

#### Option B: Manual Database Export from MAMP PRO
```bash
# Export specific database
/Applications/MAMP/Library/bin/mysqldump -u root -proot my_database > ~/my_database.sql

# Import to NKS WebDev Console
nks-wdc database import ~/my_database.sql
```

#### Option C: Using phpMyAdmin
1. **In MAMP PRO phpMyAdmin** (http://localhost:8888/phpmyadmin):
   - Select database
   - Export tab
   - Download SQL file
2. **In NKS WebDev Console phpMyAdmin** (http://localhost/phpmyadmin):
   - Import tab
   - Upload SQL file

**Verify imports**:
```bash
# List all databases
nks-wdc database list

# Check specific database
nks-wdc database info my_database
```

### Step 4: Migrate Project Files

#### Option A: Quick Copy (No Changes Needed)
If your MAMP PRO projects are in `~/Sites`:
```bash
# Create projects directory
mkdir -p ~/projects

# Copy files
cp -r ~/Sites/* ~/projects/

# Or move (if you're uninstalling MAMP PRO):
mv ~/Sites/* ~/projects/
```

#### Option B: Re-link Existing Projects
If your projects are elsewhere:
```bash
wdc site create \
  --name my-project \
  --path /Users/you/existing/path/to/my-project \
  --php 8.2 \
  --ssl true
```

### Step 5: Create Sites in NKS WebDev Console

For each MAMP PRO site, create a new NKS WebDev Console site. You have two options:

#### Option A: GUI (Recommended for Beginners)
1. Click **File → New Site**
2. Enter site name (e.g., `my-project`)
3. Point to your project path
4. Select PHP version (check MAMP PRO settings for original version)
5. Click **Create Site**

#### Option B: CLI (Recommended for Automation)
```bash
# Single site
wdc site create \
  --name my-project \
  --path /Users/you/projects/my-project \
  --php 8.2 \
  --domain my-project.local \
  --ssl true

# Multiple sites (batch create)
wdc site import ~/mamp-sites.yml
```

**Batch import file example** (`mamp-sites.yml`):
```yaml
sites:
  - name: wordpress-site
    path: /Users/you/projects/wordpress-site
    php: "7.4"
    domain: wordpress-site.local
    ssl: true

  - name: laravel-app
    path: /Users/you/projects/laravel-app
    php: "8.2"
    domain: laravel-app.local
    ssl: true

  - name: legacy-php5
    path: /Users/you/projects/legacy-php5
    php: "5.6"
    domain: legacy-php5.local
    ssl: false
```

### Step 6: Update Your Hosts File

MAMP PRO uses `127.0.0.1 localhost` for sites. NKS WebDev Console uses `.local` domains.

#### Update `/etc/hosts` (macOS/Linux)
```bash
# Add entries for all your sites
echo "127.0.0.1    my-project.local" | sudo tee -a /etc/hosts
echo "127.0.0.1    another-site.local" | sudo tee -a /etc/hosts

# Or auto-sync
wdc dns-sync
```

#### Update `hosts` file (Windows)
```
127.0.0.1    my-project.local
127.0.0.1    another-site.local
127.0.0.1    phpmyadmin.local
```

### Step 7: Update Project Configuration Files

Update your `.env` and database configuration files to point to NKS WebDev Console:

#### Laravel (.env)
```bash
# Before (MAMP PRO)
DB_HOST=127.0.0.1
DB_PORT=3306
DB_DATABASE=my_laravel_db
DB_USERNAME=root
DB_PASSWORD=root

# After (NKS WebDev Console)
# If using default MySQL port 3306:
DB_HOST=127.0.0.1
DB_PORT=3306
DB_DATABASE=my_laravel_db
DB_USERNAME=root
DB_PASSWORD=your_new_password  # The one you set in wizard
```

#### WordPress (wp-config.php)
```php
// Before
define('DB_HOST', 'localhost:3306');
define('DB_NAME', 'wordpress_db');
define('DB_USER', 'root');
define('DB_PASSWORD', 'root');

// After
define('DB_HOST', '127.0.0.1');  // Or localhost
define('DB_NAME', 'wordpress_db');
define('DB_USER', 'root');
define('DB_PASSWORD', 'your_new_password');
```

#### Symfony (.env.local)
```bash
# Before
DATABASE_URL="mysql://root:root@127.0.0.1:3306/symfony_db?serverVersion=5.7"

# After
DATABASE_URL="mysql://root:your_new_password@127.0.0.1:3306/symfony_db?serverVersion=8.0"
```

### Step 8: Test Your Sites

Visit each site in your browser to verify everything works:

```bash
# Test all sites
wdc site list  # Shows all sites and their URLs

# Visit in browser
https://my-project.local
https://another-site.local
```

**Checklist for each site**:
- [ ] Site loads without errors
- [ ] Database connections work (check logs)
- [ ] File uploads work (if applicable)
- [ ] Admin panel accessible
- [ ] Email functions work (or see [Email Configuration](#email-configuration) below)

### Step 9: Verify PHP Extensions

MAMP PRO may have had different extensions enabled. Verify NKS WebDev Console has everything you need:

```bash
# Check installed extensions
wdc php extensions

# Install missing extensions
wdc php install-extension gd intl opcache redis

# Restart PHP
nks-wdc service restart php-fpm
```

**Common extensions to verify**:
- `gd` – Image manipulation
- `intl` – Internationalization
- `opcache` – PHP caching
- `redis` – Redis client
- `imagick` – ImageMagick
- `xdebug` – Debugging (if using it)

### Step 10: Uninstall MAMP PRO (Optional)

Once everything is working in NKS WebDev Console:

```bash
# Backup MAMP PRO configuration one last time (optional)
cp -r /Applications/MAMP ~/mamp-final-backup

# Uninstall MAMP PRO
rm -rf /Applications/MAMP
rm -rf /Library/LaunchDaemons/com.appsolute.mamp*

# Remove MAMP PRO license
rm -rf ~/Library/Application\ Support/MAMP
```

## Configuration Migration

### Custom Apache Directives

If you had custom VirtualHost configurations in MAMP PRO:

**Find them**:
```bash
cat /Applications/MAMP/conf/apache/sites-enabled/*.conf
```

**Port to NKS WebDev Console**:

Instead of editing Apache directly, use NKS WebDev Console's per-site configuration:

```bash
# Edit site configuration
wdc site edit my-project --advanced

# Or add custom Nginx directives
wdc site update my-project --nginx-directives "
location ~* \.(jpg|jpeg|png|gif)$ {
    expires 30d;
    add_header Cache-Control 'public, immutable';
}
"
```

### Custom PHP Configuration

If you had custom `php.ini` settings:

```bash
# Find your MAMP PRO php.ini
cat /Applications/MAMP/conf/php7.4/php.ini

# Copy settings to NKS WebDev Console
wdc site update my-project --ini "memory_limit=512M" --ini "upload_max_filesize=100M"

# Or edit directly
wdc php edit-config
```

### SSL Certificates

MAMP PRO uses self-signed certificates stored in `/Applications/MAMP/conf/apache/ssl/`.

NKS WebDev Console auto-generates new certificates for each site. If you want to use your existing certificates:

```bash
# Import existing certificate
nks-wdc cert import my-project.local \
  --cert /Applications/MAMP/conf/apache/ssl/my-project.crt \
  --key /Applications/MAMP/conf/apache/ssl/my-project.key
```

## Email Configuration

### Local Email Testing

MAMP PRO included Postfix for local email testing. NKS WebDev Console doesn't include mail services by default, but you have options:

#### Option A: Use MailHog (Recommended)
```bash
# Install MailHog
wdc plugin install mailhog

# Configure your app to send to localhost:1025
# Web UI: http://localhost:8025
```

Update your framework config:

```php
// Laravel (.env)
MAIL_MAILER=smtp
MAIL_HOST=127.0.0.1
MAIL_PORT=1025
MAIL_FROM_ADDRESS=dev@localhost
```

#### Option B: Use Mailtrap
1. Sign up free at https://mailtrap.io
2. Get SMTP credentials
3. Update your `.env`:
```
MAIL_MAILER=smtp
MAIL_HOST=smtp.mailtrap.io
MAIL_PORT=2525
MAIL_USERNAME=your_mailtrap_username
MAIL_PASSWORD=your_mailtrap_password
```

#### Option C: Use Postfix (Advanced)
```bash
# Install via plugin
wdc plugin install postfix

# Configure app to send to localhost:25
MAIL_HOST=127.0.0.1
MAIL_PORT=25
```

## Troubleshooting Migration Issues

### Sites Don't Load After Migration

1. **Check DNS resolution**:
   ```bash
   nslookup my-project.local
   # Should return 127.0.0.1
   ```

2. **Verify services are running**:
   ```bash
   nks-wdc service status
   ```

3. **Check logs**:
   ```bash
   nks-wdc logs nginx
   nks-wdc logs php-fpm
   ```

### Database Connection Fails

```bash
# Verify MySQL is running and port is correct
nks-wdc service status mysql
wdc config get mysql.port

# Test connection directly
mysql -u root -p -h 127.0.0.1
```

### Old MAMP PRO Processes Still Running

```bash
# Kill MAMP PRO processes
sudo killall -9 Apache
sudo killall -9 mysqld
sudo killall -9 php-fpm

# Verify they're gone
ps aux | grep -E 'Apache|mysql|php'

# Start NKS WebDev Console
nks-wdc service start all
```

### Database Passwords Don't Work After Import

If you created new MySQL root password during NKS WebDev Console setup, your imported databases use MAMP PRO's old password (`root`).

**Solution**:

```bash
# Drop and recreate the user with the new password
nks-wdc database update-user-password root --password "new_password"

# Or create separate user for your apps
nks-wdc database create-user app_user --password "app_password"
nks-wdc database grant-privileges app_user my_database
```

Then update your `.env` files to use the new credentials.

## Performance Comparison

| Feature | MAMP PRO | NKS WebDev Console |
|---------|----------|----------|
| Startup time | 15–30 seconds | 3–5 seconds |
| Memory usage | 600 MB+ | 200 MB+ |
| Per-site PHP versions | Restart required | Instant |
| SSL setup | Manual | Auto-generated |
| GUI complexity | Heavy | Lightweight |
| Database size | Limited to 4 GB | Unlimited |

## Rollback (If Needed)

If something goes wrong during migration:

```bash
# Keep MAMP PRO installed for a few days
# If you need to rollback, simply restart MAMP PRO:
/Applications/MAMP/bin/start.sh

# Your data is unchanged in ~/Sites
```

## Next Steps

- [CLI Reference](./cli-reference.md) – Master NKS WebDev Console commands
- [Per-Site PHP Configuration](./guides/php.md) – Set different versions per project
- [Database Guide](./guides/databases.md) – Advanced database operations
- [Framework Integration](./guides/frameworks.md) – Laravel, WordPress, Symfony, etc.

---

## Migration Checklist

Print this and check off each step:

- [ ] Install NKS WebDev Console
- [ ] Run first-time wizard
- [ ] Backup MAMP PRO databases
- [ ] Export databases
- [ ] Import to NKS WebDev Console
- [ ] Copy project files
- [ ] Create sites in NKS WebDev Console
- [ ] Update `/etc/hosts`
- [ ] Update `.env` files
- [ ] Test each site
- [ ] Verify PHP extensions
- [ ] Configure email (MailHog or Mailtrap)
- [ ] Uninstall MAMP PRO (optional)

**Complete?** You've successfully migrated to NKS WebDev Console. Welcome to the community!

---

**Need help?** Join the Discord community: https://discord.gg/nks-wdc
