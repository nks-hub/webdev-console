# DevForge Documentation - Complete Table of Contents

## Overview

DevForge documentation is organized into several key sections. Each section builds on foundational knowledge, progressing from beginner to advanced topics.

---

## 1. Getting Started (Beginner)

### [README.md](./README.md)
- Project overview and key features
- System requirements (Windows, macOS, Linux)
- Quick start guide (3 steps)
- Links to main documentation

### [Getting Started Guide](./getting-started.md)
- **Detailed installation** for all platforms
- **First-run wizard** walkthrough
- Creating your first site (GUI and CLI methods)
- Accessing sites, databases, and CLI
- Stopping services
- Common first-time troubleshooting
- FAQ for new users

**Topics covered**:
- System requirements checklist
- Platform-specific installation (Windows, macOS, Linux)
- First-run wizard steps (5 screens)
- Virtual host creation
- SSL certificate basics
- phpMyAdmin access
- Service management (start/stop)
- Port and DNS basics

---

## 2. Core Concepts (Beginner)

### Core Concepts Guide (Not yet created)
- **Sites**: Virtual hosts, domains, directories
- **Services**: Apache, Nginx, PHP-FPM, MySQL, MariaDB
- **PHP versions**: Installation, switching, per-site configuration
- **SSL certificates**: Self-signed, trusted, renewal
- **DNS resolution**: `.local` domains, hosts file, dnsmasq
- **Port management**: Port forwarding, port conflicts
- **Service lifecycle**: Start, stop, restart, status

---

## 3. User Guides (Intermediate)

### [Sites Guide](./guides/sites.md) (Not yet created)
#### Creating & Managing Sites
- Creating a new site (GUI and CLI)
- Configuring site PHP version
- Enabling/disabling SSL
- Custom Apache/Nginx directives
- Site aliases (multiple domains)
- Site templates and scaffolding
- Linking existing projects
- Exporting/importing sites
- Deleting sites
- Site backup and restore

#### Advanced Site Features
- Subdomain wildcard configuration
- Custom server blocks (Nginx)
- VirtualHost customization (Apache)
- Port binding per site
- Performance tuning
- Caching configuration

### [PHP Management Guide](./guides/php.md) (Not yet created)
#### Installing & Switching PHP
- List available PHP versions
- Install new versions
- Set default PHP version
- Per-site PHP selection
- PHP switching without restart

#### Extensions Management
- List installed extensions
- Install extensions
- Uninstall extensions
- Extension dependencies
- Verify installed extensions

#### PHP Configuration
- Edit `php.ini` globally
- Per-site `php.ini` settings
- Memory limit configuration
- Upload file size limits
- Timezone settings
- Error reporting levels
- Xdebug setup

### [Database Guide](./guides/databases.md) (Not yet created)
#### Database Management
- MySQL vs MariaDB
- Creating databases
- Creating users
- Granting privileges
- Viewing database list
- Importing SQL files
- Exporting databases
- Database backup/restore
- phpMyAdmin access
- MySQL CLI access

#### Database Operations
- Running SQL scripts
- Database optimization
- Database replication (advanced)
- Database migration
- Character set and collation
- User password management

### [Framework Integration Guide](./guides/frameworks.md) (Not yet created)
#### Supported Frameworks
- **Laravel**: Setup, Artisan commands, queue workers
- **WordPress**: Installation, plugin management, database
- **Symfony**: Bin/console integration, Composer
- **Drupal**: Drush integration, multisite setup
- **Magento**: Elasticsearch integration, cache backends
- **Static sites**: Node.js, Hugo, Jekyll support

#### Framework-Specific Configuration
- Environment files (.env)
- Cache configuration
- Session storage
- File permissions
- Asset compilation

---

## 4. Command Line Interface (Intermediate)

### [CLI Reference](./cli-reference.md) (Not yet created)
#### Service Commands
- `devforge service start|stop|restart [service]`
- `devforge service status`
- `devforge service enable|disable` (auto-start)
- `devforge logs [service]`

#### Site Commands
- `devforge site create|delete|update`
- `devforge site list|info`
- `devforge site import|export`
- `devforge site start|stop`

#### PHP Commands
- `devforge php version`
- `devforge php install|uninstall [version]`
- `devforge php extensions`
- `devforge php install-extension|uninstall-extension`
- `devforge php config [key]` or `devforge php edit-config`

#### Database Commands
- `devforge database list|create|delete`
- `devforge database import|export`
- `devforge database create-user|delete-user`
- `devforge database grant-privileges`
- `devforge database connect` (CLI access)

#### Utility Commands
- `devforge port-check [port]`
- `devforge dns-sync`
- `devforge cert generate|export|import|regenerate`
- `devforge config get|set [key] [value]`
- `devforge diagnose`
- `devforge version`

#### Shell Completion
- Bash/Zsh/Fish setup
- Tab completion examples
- Command hints

#### Scripting Examples
- Batch site creation
- Automated backups
- Database operations
- Service orchestration
- JSON output parsing

---

## 5. Configuration Reference (Intermediate)

### [Configuration Guide](./configuration.md) (Not yet created)
#### Global Settings
- Global `config.yaml` location and structure
- Service ports configuration
- Data directory location
- Update settings
- Notification preferences
- Theme selection

#### Per-Site Configuration
- Site-specific `config.yaml` structure
- PHP version per site
- Web server selection (Apache vs Nginx)
- Port binding
- SSL configuration
- Custom directives

#### Advanced Configuration
- Environment variables
- Service resource limits
- Database configuration
- Backup schedules
- Plugin configuration
- Performance tuning

---

## 6. Troubleshooting & Support (Intermediate)

### [Troubleshooting Guide](./troubleshooting.md) ✓ (Completed)
#### By Category
1. **Port & Network Issues**
   - Port already in use
   - Network unreachable
   - Port binding failures

2. **DNS & Domain Resolution**
   - `.local` domain not resolving
   - Multiple `.local` domains
   - Wildcard domain issues

3. **SSL & HTTPS Errors**
   - Certificate not trusted
   - Certificate expired
   - Mixed content warnings

4. **PHP & Extensions**
   - Extension not available
   - Wrong PHP version running
   - Memory limit issues
   - Timezone configuration

5. **Database Connection Issues**
   - MySQL connection refused
   - Authentication failed
   - Database not found
   - phpMyAdmin issues

6. **Service Startup Failures**
   - Nginx/Apache won't start
   - PHP-FPM won't start
   - MySQL won't start

7. **Performance Issues**
   - Slow page loads
   - Slow database queries
   - File watching overhead

8. **File & Permission Errors**
   - Permission denied
   - `.htaccess` not working
   - Cannot delete files

#### Common Questions
- Detailed diagnostics commands
- Log file locations
- Support channels

---

## 7. Migration Guides (Intermediate)

### [Migrating from MAMP PRO](./migration-mamp-pro.md) ✓ (Completed)
- Detailed step-by-step migration
- Database backup and import
- Project file migration
- Host file updates
- Configuration updates (.env, wp-config.php, etc.)
- Email configuration alternatives
- Performance comparison
- Rollback instructions
- Migration checklist

### [Migrating from XAMPP](./migration-xampp.md) (Not yet created)
- XAMPP to DevForge differences
- Database migration
- Project structure conversion
- Apache configuration migration
- PHP version conversion
- Issue-specific troubleshooting

### [Migrating from WampServer](./migration-wampserver.md) (Not yet created)
- WampServer to DevForge mapping
- Database backup from WampServer
- Virtual hosts conversion
- Service configuration
- Alias migration

### [Migrating from Laragon](./migration-laragon.md) (Not yet created)
- Laragon feature parity
- Database export/import
- Site configuration migration
- Package management comparison

---

## 8. Advanced Topics (Advanced)

### [Plugin Development Guide](./plugin-development.md) (Not yet created)
#### Plugin Architecture
- Plugin structure and layout
- Plugin manifest format
- Plugin lifecycle hooks

#### Types of Plugins
- **Service plugins**: Add new services (Redis, PostgreSQL, etc.)
- **Framework drivers**: Support new frameworks
- **GUI extensions**: Extend the user interface
- **CLI extensions**: Add custom commands

#### Development
- Plugin SDK and APIs
- Testing plugins
- Debugging plugins
- Publishing to registry

### [Architecture Documentation](./architecture.md) (Not yet created)
#### System Design
- Service management architecture
- Configuration pipeline
- File structure overview
- Database schema

#### Security Model
- File permissions
- Database security
- SSL/TLS implementation
- User privilege model

#### Performance Optimization
- Caching strategies
- Resource management
- Scalability considerations

#### Contributing Guide
- Code standards
- Pull request process
- Bug reporting
- Feature requests
- Development setup

---

## 9. Quick References (All Levels)

### Quick Reference Cards (Not yet created)
- **Command cheat sheet**: Most-used CLI commands
- **Keyboard shortcuts**: GUI shortcuts
- **Keyboard shortcut legend**: Common symbols explained
- **Port reference**: Default ports for all services
- **PHP extension reference**: Common extensions and use cases
- **Error messages**: Common error codes and solutions

---

## Documentation Structure by User Level

### Beginner User Path
1. README.md
2. Getting Started Guide
3. Core Concepts
4. Sites Guide (basic)
5. Database Guide (basic)
6. Troubleshooting (common issues)

### Intermediate User Path
1. All beginner materials
2. PHP Management Guide
3. Framework Integration
4. CLI Reference
5. Configuration Reference
6. Migration Guides
7. Troubleshooting (advanced)

### Advanced User Path
1. All beginner + intermediate materials
2. Plugin Development Guide
3. Architecture Documentation
4. Contributing Guide
5. Performance tuning sections
6. Custom framework drivers

---

## Feature Coverage Matrix

| Feature | Beginner | Intermediate | Advanced |
|---------|----------|--------------|----------|
| Create site | Getting Started | Sites Guide | Plugin Dev |
| Switch PHP version | Getting Started | PHP Guide | Framework Dev |
| Manage database | Getting Started | DB Guide | Architecture |
| Use CLI | Troubleshooting | CLI Reference | Scripting |
| Migrate from MAMP | Migration | Migration | Custom Migration |
| Troubleshoot issues | Troubleshooting | Troubleshooting | Architecture |
| Extend DevForge | N/A | N/A | Plugin Dev |

---

## External Resources

### Official Links
- **Website**: https://devforge.sh
- **GitHub**: https://github.com/devforge/devforge
- **Discord Community**: https://discord.gg/devforge
- **Issue Tracker**: https://github.com/devforge/devforge/issues
- **Email Support**: support@devforge.sh (Pro tier)

### Related Documentation
- [PHP Official Docs](https://www.php.net/docs.php)
- [Apache Documentation](https://httpd.apache.org/docs/)
- [Nginx Documentation](https://nginx.org/en/docs/)
- [MySQL Documentation](https://dev.mysql.com/doc/)
- [Laravel Docs](https://laravel.com/docs)
- [WordPress Docs](https://wordpress.org/support/)

---

## Documentation Statistics

| Metric | Value |
|--------|-------|
| Total pages | 12 (6 completed) |
| Code examples | 150+ |
| Troubleshooting entries | 40+ |
| Supported frameworks | 6+ |
| Commands documented | 50+ |
| Platform support | 3 (Windows, macOS, Linux) |

---

## How to Use This Documentation

1. **New to DevForge?** → Start with [README.md](./README.md) and [Getting Started](./getting-started.md)
2. **Creating sites?** → See [Sites Guide](./guides/sites.md)
3. **Working with PHP?** → See [PHP Management](./guides/php.md)
4. **Having issues?** → See [Troubleshooting](./troubleshooting.md)
5. **Migrating from MAMP PRO?** → See [Migration Guide](./migration-mamp-pro.md)
6. **Building plugins?** → See [Plugin Development](./plugin-development.md)

---

**Last updated**: 2026-04-09
**Version**: 2.0.0 Documentation
