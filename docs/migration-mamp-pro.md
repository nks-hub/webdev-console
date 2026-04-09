# Migrating from MAMP PRO to DevForge

This guide helps you move your projects, databases, and configurations from MAMP PRO to DevForge with minimal downtime.

**Estimated migration time**: 30 minutes for typical setup with 5–10 projects

## Why Migrate from MAMP PRO?

- **Simpler interface** – DevForge focuses on essential features
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

### Step 1: Install DevForge

**macOS**:
```bash
brew tap devforge/tap
brew install devforge
```

**Windows**:
```bash
winget install devforge
# or download from https://devforge.sh/download
```

### Step 2: Run First-Time Wizard

Launch DevForge. The wizard will:
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
# Restore your MAMP PRO backup to DevForge
devforge database import ~/mamp-backup.sql
```

#### Option B: Manual Database Export from MAMP PRO
```bash
# Export specific database
/Applications/MAMP/Library/bin/mysqldump -u root -proot my_database > ~/my_database.sql

# Import to DevForge
devforge database import ~/my_database.sql
```

#### Option C: Using phpMyAdmin
1. **In MAMP PRO phpMyAdmin** (http://localhost:8888/phpmyadmin):
   - Select database
   - Export tab
   - Download SQL file
2. **In DevForge phpMyAdmin** (http://localhost/phpmyadmin):
   - Import tab
   - Upload SQL file

**Verify imports**:
```bash
# List all databases
devforge database list

# Check specific database
devforge database info my_database
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
devforge site create \
  --name my-project \
  --path /Users/you/existing/path/to/my-project \
  --php 8.2 \
  --ssl true
```

### Step 5: Create Sites in DevForge

For each MAMP PRO site, create a new DevForge site. You have two options:

#### Option A: GUI (Recommended for Beginners)
1. Click **File → New Site**
2. Enter site name (e.g., `my-project`)
3. Point to your project path
4. Select PHP version (check MAMP PRO settings for original version)
5. Click **Create Site**

#### Option B: CLI (Recommended for Automation)
```bash
# Single site
devforge site create \
  --name my-project \
  --path /Users/you/projects/my-project \
  --php 8.2 \
  --domain my-project.local \
  --ssl true

# Multiple sites (batch create)
devforge site import ~/mamp-sites.yml
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

MAMP PRO uses `127.0.0.1 localhost` for sites. DevForge uses `.local` domains.

#### Update `/etc/hosts` (macOS/Linux)
```bash
# Add entries for all your sites
echo "127.0.0.1    my-project.local" | sudo tee -a /etc/hosts
echo "127.0.0.1    another-site.local" | sudo tee -a /etc/hosts

# Or auto-sync
devforge dns-sync
```

#### Update `hosts` file (Windows)
```
127.0.0.1    my-project.local
127.0.0.1    another-site.local
127.0.0.1    phpmyadmin.local
```

### Step 7: Update Project Configuration Files

Update your `.env` and database configuration files to point to DevForge:

#### Laravel (.env)
```bash
# Before (MAMP PRO)
DB_HOST=127.0.0.1
DB_PORT=3306
DB_DATABASE=my_laravel_db
DB_USERNAME=root
DB_PASSWORD=root

# After (DevForge)
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
devforge site list  # Shows all sites and their URLs

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

MAMP PRO may have had different extensions enabled. Verify DevForge has everything you need:

```bash
# Check installed extensions
devforge php extensions

# Install missing extensions
devforge php install-extension gd intl opcache redis

# Restart PHP
devforge service restart php-fpm
```

**Common extensions to verify**:
- `gd` – Image manipulation
- `intl` – Internationalization
- `opcache` – PHP caching
- `redis` – Redis client
- `imagick` – ImageMagick
- `xdebug` – Debugging (if using it)

### Step 10: Uninstall MAMP PRO (Optional)

Once everything is working in DevForge:

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

**Port to DevForge**:

Instead of editing Apache directly, use DevForge's per-site configuration:

```bash
# Edit site configuration
devforge site edit my-project --advanced

# Or add custom Nginx directives
devforge site update my-project --nginx-directives "
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

# Copy settings to DevForge
devforge site update my-project --ini "memory_limit=512M" --ini "upload_max_filesize=100M"

# Or edit directly
devforge php edit-config
```

### SSL Certificates

MAMP PRO uses self-signed certificates stored in `/Applications/MAMP/conf/apache/ssl/`.

DevForge auto-generates new certificates for each site. If you want to use your existing certificates:

```bash
# Import existing certificate
devforge cert import my-project.local \
  --cert /Applications/MAMP/conf/apache/ssl/my-project.crt \
  --key /Applications/MAMP/conf/apache/ssl/my-project.key
```

## Email Configuration

### Local Email Testing

MAMP PRO included Postfix for local email testing. DevForge doesn't include mail services by default, but you have options:

#### Option A: Use MailHog (Recommended)
```bash
# Install MailHog
devforge plugin install mailhog

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
devforge plugin install postfix

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
   devforge service status
   ```

3. **Check logs**:
   ```bash
   devforge logs nginx
   devforge logs php-fpm
   ```

### Database Connection Fails

```bash
# Verify MySQL is running and port is correct
devforge service status mysql
devforge config get mysql.port

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

# Start DevForge
devforge service start all
```

### Database Passwords Don't Work After Import

If you created new MySQL root password during DevForge setup, your imported databases use MAMP PRO's old password (`root`).

**Solution**:

```bash
# Drop and recreate the user with the new password
devforge database update-user-password root --password "new_password"

# Or create separate user for your apps
devforge database create-user app_user --password "app_password"
devforge database grant-privileges app_user my_database
```

Then update your `.env` files to use the new credentials.

## Performance Comparison

| Feature | MAMP PRO | DevForge |
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

- [CLI Reference](./cli-reference.md) – Master DevForge commands
- [Per-Site PHP Configuration](./guides/php.md) – Set different versions per project
- [Database Guide](./guides/databases.md) – Advanced database operations
- [Framework Integration](./guides/frameworks.md) – Laravel, WordPress, Symfony, etc.

---

## Migration Checklist

Print this and check off each step:

- [ ] Install DevForge
- [ ] Run first-time wizard
- [ ] Backup MAMP PRO databases
- [ ] Export databases
- [ ] Import to DevForge
- [ ] Copy project files
- [ ] Create sites in DevForge
- [ ] Update `/etc/hosts`
- [ ] Update `.env` files
- [ ] Test each site
- [ ] Verify PHP extensions
- [ ] Configure email (MailHog or Mailtrap)
- [ ] Uninstall MAMP PRO (optional)

**Complete?** You've successfully migrated to DevForge. Welcome to the community!

---

**Need help?** Join the Discord community: https://discord.gg/devforge
