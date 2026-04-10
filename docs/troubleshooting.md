# Troubleshooting Guide

This guide covers common issues and their solutions. Use the search feature or table of contents below to find your problem.

## Table of Contents

1. [Port & Network Issues](#port--network-issues)
2. [DNS & Domain Resolution](#dns--domain-resolution)
3. [SSL & HTTPS Errors](#ssl--https-errors)
4. [PHP & Extensions](#php--extensions)
5. [Database Connection Issues](#database-connection-issues)
6. [Service Startup Failures](#service-startup-failures)
7. [Performance Issues](#performance-issues)
8. [File & Permission Errors](#file--permission-errors)

---

## Port & Network Issues

### Port 80/443 Already in Use

**Symptom**: Installation or service startup fails with "Address already in use"

**Diagnosis**:
```bash
nks-wdc port-check 80 443 3306 9000
```

**Solutions**:

1. **Identify the conflicting process**:
   - Windows: `netstat -ano | findstr ":80"`
   - macOS/Linux: `lsof -i :80`

2. **Change NKS WebDev Console port**:
   ```bash
   wdc config set ports.http 8080
   wdc config set ports.https 8443
   nks-wdc service restart
   ```
   Then access sites via `https://my-project.local:8443`

3. **Stop the conflicting service**:
   - If MAMP PRO: `sudo /Applications/MAMP/bin/stop.sh`
   - If Apache (macOS): `sudo apachectl stop`
   - If IIS (Windows): `net stop W3SVC`

4. **Restart NKS WebDev Console**:
   ```bash
   nks-wdc service restart all
   ```

### Network Unreachable

**Symptom**: Cannot access `my-project.local` from another machine on network

**Root Cause**: NKS WebDev Console binds to `127.0.0.1` (localhost) by default, only accessible locally.

**Solution**:

1. **Bind to all interfaces**:
   ```bash
   wdc config set server.bind 0.0.0.0
   nks-wdc service restart nginx
   ```

2. **Access from other machine**:
   ```bash
   curl https://192.168.x.x:8443  # Use your PC's IP
   ```

3. **Add DNS entry on other machine** `/etc/hosts`:
   ```
   192.168.1.100    my-project.local
   ```

---

## DNS & Domain Resolution

### `.local` Domain Not Resolving

**Symptom**: Browser shows "This site can't be reached" for `*.local` domains

**Windows Solution**:
```bash
# Check hosts file
type %WINDIR%\System32\drivers\etc\hosts

# Add manually if missing:
127.0.0.1    my-project.local
```

**macOS/Linux Solution**:
```bash
# Auto-sync DNS
wdc dns-sync

# Or add to /etc/hosts manually:
echo "127.0.0.1    my-project.local" | sudo tee -a /etc/hosts

# Verify resolution
nslookup my-project.local
```

### Multiple `.local` Domains Not Working

**Symptom**: First site works, second site (same TLD) doesn't resolve

**Solution** (Recommended):
```bash
# Use different TLDs instead:
wdc site create --name project1 --domain project1.local
wdc site create --name project2 --domain project2.test
wdc site create --name project3 --domain project3.dev
```

Then add all to `/etc/hosts`:
```
127.0.0.1    project1.local
127.0.0.1    project2.test
127.0.0.1    project3.dev
```

### Wildcard `*.local` Not Working

**Symptom**: `sub.my-project.local` returns 404 even though main domain works

**Windows Fix** - Update hosts file:
```
127.0.0.1    my-project.local
127.0.0.1    *.my-project.local
```
Windows doesn't support wildcard entries in hosts. Instead, add each subdomain individually:
```
127.0.0.1    api.my-project.local
127.0.0.1    cdn.my-project.local
```

**macOS/Linux Fix** - Use dnsmasq:
```bash
# Install dnsmasq (macOS)
brew install dnsmasq

# Configure wildcard:
echo 'address=/my-project.local/127.0.0.1' | sudo tee /usr/local/etc/dnsmasq.conf

# Restart:
sudo brew services restart dnsmasq
```

---

## SSL & HTTPS Errors

### Certificate Not Trusted (NET::ERR_CERT_AUTHORITY_INVALID)

**Symptom**: Browser shows warning on first HTTPS visit to site

**This is normal and expected** for self-signed certificates. Solution:

1. **Chrome/Edge**: Click "Advanced" → "Proceed to my-project.local (unsafe)"
2. **Safari**: Click "Show Details" → "visit this website"
3. **Firefox**: Click "Advanced..." → "Accept Risk and Continue"

### Permanently Trust Certificate (Optional)

**Windows**:
```bash
# Export certificate
nks-wdc cert export my-project.local

# Import to Trusted Root (requires admin, run PowerShell as admin):
Import-Certificate -FilePath "my-project.local.crt" -CertStoreLocation "Cert:\LocalMachine\Root"
```

**macOS**:
```bash
# Export certificate
nks-wdc cert export my-project.local

# Add to Keychain
sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain my-project.local.crt

# Restart Chrome to pick up new trust
```

**Linux**:
```bash
# Copy certificate to system trust store (Ubuntu/Debian)
sudo cp my-project.local.crt /usr/local/share/ca-certificates/
sudo update-ca-certificates
```

### Certificate Expired

**Symptom**: Certificate warning persists even after accepting it once

**Solution**:
```bash
# Regenerate certificate
nks-wdc cert regenerate my-project.local

# Or delete and recreate site (keeps files):
wdc site delete my-project.local
wdc site create --name my-project --path /existing/path --ssl true
```

### Mixed Content Warning (HTTP/HTTPS)

**Symptom**: HTTPS site shows warning about "insecure content"

**Cause**: Site loads HTTP resources (images, CSS, JS) on HTTPS page.

**Solution** (in your site code):
```php
// Use protocol-relative URLs
<link rel="stylesheet" href="//my-project.local/css/style.css">
<img src="//my-project.local/images/logo.png" alt="Logo">

// Or use https:// explicitly
<script src="https://my-project.local/js/app.js"></script>
```

**Or force HTTPS redirect**:
```nginx
# In your Nginx config or .htaccess
if ($scheme != "https") {
    return 301 https://$server_name$request_uri;
}
```

---

## PHP & Extensions

### PHP Extension Not Available

**Symptom**: Fatal error "Call to undefined function curl_init()" or similar

**Diagnosis**:
```bash
# List installed extensions
wdc php extensions

# Check if extension is available
wdc php extensions | grep curl
```

**Solution**:
```bash
# Install extension
wdc php install-extension curl

# Or install multiple:
wdc php install-extension gd intl opcache

# Restart PHP
nks-wdc service restart php-fpm

# Verify in browser: <?php phpinfo(); ?>
```

### Wrong PHP Version Running

**Symptom**: `phpinfo()` shows PHP 7.4 but site should use PHP 8.2

**Diagnosis**:
```bash
# Check site's PHP version
wdc site info my-project | grep "PHP Version"

# Check default PHP version
wdc php version
```

**Solution**:
```bash
# Update site to use different PHP
wdc site update my-project --php 8.2

# Restart web server
nks-wdc service restart nginx
```

### PHP Memory Limit Too Low

**Symptom**: "Fatal error: Allowed memory size exhausted" during deployment or large operations

**Solution**:
```bash
# Check current limit
wdc php config memory_limit

# Increase limit (per site)
wdc site update my-project --ini "memory_limit=512M"

# Or edit global php.ini
wdc php edit-config

# Restart PHP
nks-wdc service restart php-fpm
```

### PHP Timezone Not Set

**Symptom**: Date functions show UTC, should show local timezone

**Solution**:
```bash
# Set timezone globally
wdc php config set date.timezone "Europe/Prague"

# Or per-site in php.ini
wdc site update my-project --ini "date.timezone=Europe/Prague"

# Verify in phpinfo()
```

---

## Database Connection Issues

### MySQL Connection Refused

**Symptom**: "Connection refused" (Connection to 127.0.0.1:3306 failed)

**Diagnosis**:
```bash
# Check if MySQL is running
nks-wdc service status mysql

# Check MySQL port
wdc config get mysql.port

# Try connecting directly
mysql -u root -p -h 127.0.0.1 -P 3306
```

**Solution**:

1. **Start MySQL**:
   ```bash
   nks-wdc service start mysql
   ```

2. **Check password** (if you forgot it):
   ```bash
   # Reset root password
   nks-wdc database reset-root-password
   ```

3. **Change connection port** (if 3306 is in use):
   ```bash
   wdc config set mysql.port 3307
   nks-wdc service restart mysql
   
   # Update your .env or connection string to use port 3307
   ```

### Authentication Failed (Access Denied)

**Symptom**: "Access denied for user 'root'@'localhost'"

**Solution**:
```bash
# 1. Try with password
mysql -u root -p -h 127.0.0.1

# 2. If you forgot password, reset it
nks-wdc database reset-root-password

# 3. Create user with specific permissions
nks-wdc database create-user my_user --password "secure_pass"
nks-wdc database grant-privileges my_user my_database
```

### Database Not Found After Site Creation

**Symptom**: Site created but database doesn't exist

**Solution**:
```bash
# Create database manually
nks-wdc database create my_project_db

# Or through phpMyAdmin:
# 1. Visit http://localhost/phpmyadmin
# 2. Databases tab → Create new database
```

### phpMyAdmin Shows "No Databases"

**Symptom**: phpMyAdmin login works but no databases are visible

**Cause**: User doesn't have database privileges

**Solution**:
```bash
# Grant privileges to root user (usually already done)
nks-wdc database grant-privileges root '*'

# Or create new admin user
nks-wdc database create-user admin --password "pass123" --admin
```

---

## Service Startup Failures

### Nginx/Apache Won't Start

**Symptom**: Service shows "Failed to start" in NKS WebDev Console UI

**Diagnosis**:
```bash
# Check service logs
nks-wdc logs nginx  # or apache

# Check configuration syntax
nks-wdc nginx validate-config
```

**Solutions**:

1. **Check for port conflicts**:
   ```bash
   nks-wdc port-check 80 443
   ```

2. **Validate configuration**:
   ```bash
   nks-wdc nginx validate-config
   ```

3. **Check logs for specific error**:
   ```bash
   tail -f ~/.local/share/nks-wdc/logs/nginx.log
   ```

4. **Reset to default config**:
   ```bash
   nks-wdc nginx reset-config
   nks-wdc service restart nginx
   ```

### PHP-FPM Won't Start

**Symptom**: "PHP-FPM failed to start" error

**Solution**:
```bash
# Check logs
nks-wdc logs php-fpm

# Verify PHP binary exists
wdc php which

# Try restarting
nks-wdc service stop php-fpm
sleep 2
nks-wdc service start php-fpm

# If still fails, reset PHP
wdc php reset-config
```

### MySQL Won't Start on Windows

**Symptom**: MySQL fails to start on fresh Windows installation

**Cause**: Often Windows Defender Firewall or port conflicts

**Solutions**:

1. **Disable Firewall temporarily**:
   ```powershell
   # Run as admin
   netsh advfirewall set allprofiles state off
   nks-wdc service start mysql
   ```

2. **Add firewall exception**:
   ```powershell
   # Run as admin
   New-NetFirewallRule -DisplayName "NKS WebDev Console MySQL" -Direction Inbound -LocalPort 3306 -Protocol TCP -Action Allow
   ```

3. **Restart NKS WebDev Console service**:
   ```powershell
   # Run as admin
   nks-wdc service restart all
   ```

---

## Performance Issues

### Site Loads Very Slowly

**Symptom**: Page takes 5+ seconds to load, even for simple sites

**Common Causes**:

1. **Project on slow storage**:
   ```bash
   # Check if on external drive or network share
   wdc site info my-project
   
   # Move to SSD
   wdc site move my-project --path "C:\Projects\my-project"
   ```

2. **PHP opcache disabled**:
   ```bash
   # Check if enabled
   wdc php extensions | grep opcache
   
   # Enable if missing
   wdc php install-extension opcache
   ```

3. **File watching for too many files**:
   ```bash
   # Increase inotify limit (macOS/Linux)
   echo "fs.inotify.max_user_watches=524288" | sudo tee -a /etc/sysctl.conf
   sudo sysctl -p
   ```

4. **Too many open files** (macOS):
   ```bash
   # Check limit
   ulimit -n
   
   # Increase
   echo "ulimit -n 4096" >> ~/.zshrc
   ```

### Database Queries Very Slow

**Symptom**: SELECT queries take multiple seconds

**Solutions**:

1. **Check database fragmentation**:
   ```bash
   nks-wdc database optimize my_database
   ```

2. **Create missing indexes**:
   ```bash
   # Use phpMyAdmin to add indexes to frequently queried columns
   ```

3. **Check query using explain**:
   ```sql
   EXPLAIN SELECT * FROM users WHERE email = 'user@example.com';
   ```

4. **Restart MySQL to clear cache**:
   ```bash
   nks-wdc service restart mysql
   ```

---

## File & Permission Errors

### Permission Denied When Writing Files

**Symptom**: "Permission denied" when site tries to create files/folders

**Windows Solution**:
```powershell
# Run NKS WebDev Console as Administrator
# or grant folder permissions:
icacls "C:\path\to\project" /grant:r "Users:(OI)(CI)F"
```

**macOS/Linux Solution**:
```bash
# Check current permissions
ls -la /path/to/project

# Fix permissions (make writable by web server)
sudo chown -R _www:_www /path/to/project  # macOS
sudo chown -R www-data:www-data /path/to/project  # Linux
sudo chmod -R 755 /path/to/project
```

### .htaccess Not Working

**Symptom**: `.htaccess` rules ignored, rewrites don't apply

**Cause**: Using Nginx instead of Apache, or Apache not configured for .htaccess

**Solution**:

1. **Switch to Apache**:
   ```bash
   wdc site update my-project --server apache
   nks-wdc service restart apache
   ```

2. **Or convert `.htaccess` to Nginx rules**:
   ```nginx
   # .htaccess rule:
   # RewriteRule ^(.*)$ index.php [L]
   
   # Nginx equivalent:
   location / {
       try_files $uri $uri/ /index.php?$query_string;
   }
   ```

### Cannot Delete Site Files

**Symptom**: "File is in use" or "Permission denied" when trying to delete project folder

**Windows Solution**:
```powershell
# NKS WebDev Console is locking files. Stop web server first:
nks-wdc service stop nginx
nks-wdc service stop php-fpm

# Then delete folder
rmdir /s "C:\path\to\project"
```

**macOS/Linux Solution**:
```bash
# Stop web server
nks-wdc service stop nginx
nks-wdc service stop php-fpm

# Delete folder
rm -rf /path/to/project
```

---

## Getting More Help

- **Check logs**: `nks-wdc logs [service]` (nginx, php-fpm, mysql, etc.)
- **Run diagnostics**: `nks-wdc diagnose`
- **Community**: https://discord.gg/nks-wdc
- **GitHub Issues**: https://github.com/nks-wdc/nks-wdc/issues

---

**Still stuck?** Gather this information and ask for help:
```bash
nks-wdc diagnose > ~/nks-wdc-diagnostics.txt
```

Include the output when posting to Discord or GitHub.
