# DevForge Documentation Index

**Welcome to DevForge!** This is your complete documentation for managing local development servers across Windows, macOS, and Linux.

---

## Quick Navigation

### Getting Started (Start Here!)
- **[README.md](./README.md)** – What is DevForge and why you need it
- **[Getting Started Guide](./getting-started.md)** – Install DevForge and create your first site in 15 minutes

### Core Documentation
- **[Troubleshooting Guide](./troubleshooting.md)** – Fix common issues (40+ solutions)
- **[Table of Contents](./TABLE-OF-CONTENTS.md)** – Complete roadmap of all documentation
- **[Documentation Summary](./DOCUMENTATION-SUMMARY.md)** – What we've created and what's planned

### Migrating from Other Tools
- **[From MAMP PRO](./migration-mamp-pro.md)** – Step-by-step migration with database import
- Coming soon: XAMPP, WampServer, Laragon guides

---

## Find What You Need

### By Use Case

#### "I'm new to DevForge"
1. Read [README.md](./README.md) (5 min)
2. Follow [Getting Started Guide](./getting-started.md) (15 min)
3. Create your first site
4. Bookmark [Troubleshooting](./troubleshooting.md)

#### "I'm switching from MAMP PRO"
1. Follow [Migration from MAMP PRO](./migration-mamp-pro.md) (30 min)
2. Refer to [Troubleshooting](./troubleshooting.md) if issues arise
3. Check [Getting Started](./getting-started.md) for unfamiliar features

#### "I need to fix a problem"
1. Go to [Troubleshooting](./troubleshooting.md)
2. Find your issue in the table of contents
3. Follow the solution steps
4. If still stuck, scroll to "Getting More Help"

#### "I want to learn all features"
1. Read [Table of Contents](./TABLE-OF-CONTENTS.md) for the full roadmap
2. Follow the "Beginner User Path" section
3. Progress to "Intermediate User Path" for advanced features

---

## Documentation by Topic

### Installation & Setup
- System requirements (Windows, macOS, Linux)
- Platform-specific installation (3 methods each)
- First-run wizard walkthrough
- First site creation

→ See: [Getting Started Guide](./getting-started.md)

### Creating & Managing Sites
- Creating a new site (GUI and CLI)
- Per-site PHP version selection
- SSL certificate setup
- Custom domain configuration
- Site backup/restore

→ Coming soon: Sites Management Guide

### PHP Management
- Installing PHP versions
- Switching default PHP version
- Managing extensions
- Configuring php.ini
- Xdebug setup

→ Coming soon: PHP Management Guide

### Database Operations
- Creating databases
- Creating users and permissions
- Importing/exporting SQL
- phpMyAdmin access
- MySQL CLI access
- Database backup/restore

→ Coming soon: Database Management Guide

### Command Line Interface
- Service management commands
- Site management commands
- PHP commands
- Database commands
- Utility commands
- Shell completion setup
- Scripting examples

→ Coming soon: CLI Reference Guide

### Troubleshooting
- Port and network issues
- DNS resolution problems
- SSL/HTTPS errors
- PHP extension issues
- Database connection problems
- Service startup failures
- Performance issues
- File permission errors

→ See: [Troubleshooting Guide](./troubleshooting.md)

### Migration from Other Tools
- From MAMP PRO (detailed with database import)
- From XAMPP (planned)
- From WampServer (planned)
- From Laragon (planned)

→ See: [Migration from MAMP PRO](./migration-mamp-pro.md)

### Configuration
- Global settings
- Per-site configuration
- Port configuration
- SSL configuration
- Service resource limits

→ Coming soon: Configuration Reference

### Framework Integration
- Laravel setup and configuration
- WordPress installation and management
- Symfony and Drupal support
- Static site generators

→ Coming soon: Framework Integration Guide

### Advanced Topics
- Plugin development
- System architecture
- Contributing to DevForge
- Performance optimization

→ Coming soon: Plugin Development Guide & Architecture Docs

---

## Quick Reference

### Most Useful Links
| Task | Document |
|------|----------|
| Install DevForge | [Getting Started](./getting-started.md) |
| Create a site | [Getting Started](./getting-started.md) |
| Fix a problem | [Troubleshooting](./troubleshooting.md) |
| Migrate from MAMP | [Migration Guide](./migration-mamp-pro.md) |
| See all docs | [Table of Contents](./TABLE-OF-CONTENTS.md) |

### Common Troubleshooting Topics
| Problem | Solution |
|---------|----------|
| Port already in use | [Troubleshooting → Port Issues](./troubleshooting.md#port--network-issues) |
| DNS not resolving | [Troubleshooting → DNS Issues](./troubleshooting.md#dns--domain-resolution) |
| SSL certificate warning | [Troubleshooting → SSL Issues](./troubleshooting.md#ssl--https-errors) |
| PHP extension missing | [Troubleshooting → PHP Issues](./troubleshooting.md#php--extensions) |
| Database won't connect | [Troubleshooting → Database Issues](./troubleshooting.md#database-connection-issues) |
| Service won't start | [Troubleshooting → Service Issues](./troubleshooting.md#service-startup-failures) |
| Site loads slowly | [Troubleshooting → Performance](./troubleshooting.md#performance-issues) |
| Permission errors | [Troubleshooting → File Issues](./troubleshooting.md#file--permission-errors) |

---

## Documentation Status

### Completed (Ready to Use)
- [x] README.md – Project overview
- [x] Getting Started Guide – Installation and first site
- [x] Troubleshooting Guide – 40+ issue solutions
- [x] Migration from MAMP PRO – Detailed step-by-step
- [x] Table of Contents – Complete roadmap
- [x] Documentation Summary – Overview of what's created
- [x] Index (this file) – Navigation guide

### In Progress
- [ ] Sites Management Guide
- [ ] PHP Management Guide
- [ ] Database Management Guide
- [ ] CLI Reference
- [ ] Configuration Reference

### Planned
- [ ] Framework Integration Guide
- [ ] Migration from XAMPP
- [ ] Migration from WampServer
- [ ] Migration from Laragon
- [ ] Plugin Development Guide
- [ ] Architecture Documentation
- [ ] Quick Reference Cards

---

## Help & Support

### Getting Help
- **For issues**: See [Troubleshooting Guide](./troubleshooting.md)
- **For commands**: See CLI Reference (coming soon)
- **For settings**: See Configuration Reference (coming soon)
- **For everything**: See [Table of Contents](./TABLE-OF-CONTENTS.md)

### Support Channels
- **Discord Community**: https://discord.gg/devforge
- **GitHub Issues**: https://github.com/devforge/devforge/issues
- **Email Support**: support@devforge.sh (Pro tier)
- **Documentation**: https://docs.devforge.sh

### Diagnostic Command
If you need to report a problem, gather system information:
```bash
devforge diagnose
```
Include the output when asking for help.

---

## Keyboard Navigation

### Within This Documentation
- **Ctrl+F** (Cmd+F on macOS) – Search documentation
- **Click headings** – Jump to sections
- **Follow links** – Navigate between guides
- **Use table of contents** – See all available docs

### In DevForge
- **[Getting Started](./getting-started.md)** – GUI keyboard shortcuts (coming soon)
- **[CLI Reference](./cli-reference.md)** – All command examples (coming soon)

---

## Contributing

Found an issue in the documentation? Want to improve it?

→ Open an issue or PR at: https://github.com/devforge/devforge

---

## Version Information

- **DevForge Version**: 2.0.0
- **Documentation Last Updated**: 2026-04-09
- **Documentation Coverage**: 6 complete guides, 8 planned

---

## Start Here

**First time?** → Read [README.md](./README.md) (5 minutes)  
**Ready to install?** → Follow [Getting Started](./getting-started.md) (15 minutes)  
**Having issues?** → Check [Troubleshooting](./troubleshooting.md)  
**Switching from MAMP?** → See [Migration Guide](./migration-mamp-pro.md)  

---

**Welcome to DevForge! Happy coding! 🚀**

Questions? Visit our [Discord community](https://discord.gg/devforge) or check the [Troubleshooting Guide](./troubleshooting.md).
