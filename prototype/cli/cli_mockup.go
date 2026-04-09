// cli_mockup.go — DevForge CLI output simulator
//
// This program renders realistic CLI output for design review.
// No actual service management — purely a UI/UX mockup.
//
// Usage:
//   go run cli_mockup.go status
//   go run cli_mockup.go site:list
//   go run cli_mockup.go site:create myapp.test
//   go run cli_mockup.go php:list
//   go run cli_mockup.go ssl:status
//   go run cli_mockup.go db:list
//   go run cli_mockup.go doctor
//   go run cli_mockup.go start
//   go run cli_mockup.go stop apache
//   go run cli_mockup.go php:install 8.4

package main

import (
	"fmt"
	"math/rand"
	"os"
	"strings"
	"time"

	"github.com/fatih/color"
	"github.com/olekukonko/tablewriter"
	"github.com/schollz/progressbar/v3"
)

// ─── color helpers ───────────────────────────────────────────────────────────

var (
	green   = color.New(color.FgGreen, color.Bold)
	red     = color.New(color.FgRed, color.Bold)
	yellow  = color.New(color.FgYellow, color.Bold)
	blue    = color.New(color.FgCyan, color.Bold)
	magenta = color.New(color.FgMagenta, color.Bold)
	white   = color.New(color.FgWhite, color.Bold)
	gray    = color.New(color.FgWhite)
	dim     = color.New(color.Faint)
)

func statusIcon(status string) string {
	switch status {
	case "running":
		return green.Sprint("✓ running")
	case "stopped":
		return red.Sprint("✗ stopped")
	case "restarting":
		return yellow.Sprint("⊙ restarting")
	case "disabled":
		return dim.Sprint("○ disabled")
	case "active":
		return green.Sprint("✓ active")
	case "degraded":
		return yellow.Sprint("⚡ degraded")
	default:
		return dim.Sprint(status)
	}
}

func sslIcon(enabled bool) string {
	if enabled {
		return green.Sprint("✓")
	}
	return red.Sprint("✗")
}

func step(n, total int, desc string) {
	fmt.Printf("  %s %s", dim.Sprintf("[%d/%d]", n, total), desc)
}

func stepDone() {
	fmt.Printf("  %s\n", green.Sprint("✓"))
}

func stepLine(n, total int, desc string) {
	padded := fmt.Sprintf("%-45s", desc)
	fmt.Printf("  %s %s  %s\n",
		dim.Sprintf("[%d/%d]", n, total),
		padded,
		green.Sprint("✓"),
	)
}

func hint(msg string) {
	fmt.Printf("\n  %s %s\n", dim.Sprint("Hint:"), dim.Sprint(msg))
}

func printDivider(width int) {
	fmt.Printf("  %s\n", dim.Sprint(strings.Repeat("─", width)))
}

func sleep(ms int) {
	time.Sleep(time.Duration(ms) * time.Millisecond)
}

// ─── devforge status ─────────────────────────────────────────────────────────

func cmdStatus() {
	fmt.Printf("\n  %s  %s  %s\n\n",
		white.Sprint("DevForge v1.0.0"),
		green.Sprint("●"),
		green.Sprint("All systems operational"),
	)

	table := tablewriter.NewWriter(os.Stdout)
	table.SetHeader([]string{"SERVICE", "STATUS", "PID", "UPTIME", "MEMORY"})
	table.SetBorder(false)
	table.SetColumnSeparator("  ")
	table.SetHeaderLine(true)
	table.SetHeaderAlignment(tablewriter.ALIGN_LEFT)
	table.SetAlignment(tablewriter.ALIGN_LEFT)
	table.SetTablePadding("  ")
	table.SetNoWhiteSpace(true)
	table.SetCenterSeparator("─")
	table.SetHeaderColor(
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
	)

	rows := [][]string{
		{"Apache 2.4.58", statusIcon("running"), "12847", "2h 14m", "48 MB"},
		{"PHP-FPM 8.3", statusIcon("running"), "12851", "2h 14m", "31 MB"},
		{"PHP-FPM 8.2", statusIcon("running"), "12855", "2h 14m", "32 MB"},
		{"PHP-FPM 8.1", statusIcon("stopped"), "—", "—", "—"},
		{"MySQL 8.0", statusIcon("running"), "12860", "2h 14m", "312 MB"},
		{"Redis 7.2", statusIcon("running"), "12864", "2h 14m", "4 MB"},
		{"Mailpit 1.14", statusIcon("running"), "12868", "2h 14m", "12 MB"},
	}
	for _, r := range rows {
		table.Append(r)
	}
	table.Render()

	fmt.Printf("\n  %s\n",
		dim.Sprint("7 services  ·  5 running  ·  1 stopped  ·  1 disabled"),
	)
	hint("Use 'devforge start php-fpm@8.1' to start PHP-FPM 8.1")
	fmt.Println()
}

// ─── devforge site:list ───────────────────────────────────────────────────────

func cmdSiteList() {
	fmt.Printf("\n  %s\n\n", dim.Sprint("3 sites configured"))

	table := tablewriter.NewWriter(os.Stdout)
	table.SetHeader([]string{"DOMAIN", "PHP", "STATUS", "SSL", "ROOT"})
	table.SetBorder(false)
	table.SetColumnSeparator("  ")
	table.SetHeaderLine(true)
	table.SetHeaderAlignment(tablewriter.ALIGN_LEFT)
	table.SetAlignment(tablewriter.ALIGN_LEFT)
	table.SetTablePadding("  ")
	table.SetNoWhiteSpace(true)
	table.SetHeaderColor(
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
	)

	rows := [][]string{
		{"myapp.test", blue.Sprint("8.2"), statusIcon("active"), sslIcon(true), dim.Sprint("~/sites/myapp")},
		{"api.local", blue.Sprint("8.3"), statusIcon("active"), sslIcon(true), dim.Sprint("~/sites/api")},
		{"legacy.test", dim.Sprint("7.4"), statusIcon("stopped"), sslIcon(false), dim.Sprint("~/sites/legacy")},
	}
	for _, r := range rows {
		table.Append(r)
	}
	table.Render()

	hint("Use 'devforge site:info <domain>' for detailed info on a site")
	fmt.Println()
}

// ─── devforge site:create ────────────────────────────────────────────────────

func cmdSiteCreate(domain string) {
	fmt.Printf("\n  %s %s\n\n",
		white.Sprint("Creating site"),
		magenta.Sprint(domain),
	)

	steps := []string{
		"Validating domain ...",
		"Generating SSL certificate (mkcert) ...",
		"Writing Apache vhost config ...",
		"Updating /etc/hosts ...",
		"Reloading Apache ...",
	}

	for i, s := range steps {
		padded := fmt.Sprintf("%-45s", s)
		fmt.Printf("  %s %s", dim.Sprintf("[%d/%d]", i+1, len(steps)), padded)
		sleep(220 + rand.Intn(150))
		fmt.Printf("%s\n", green.Sprint("✓"))
	}

	printDivider(55)
	fmt.Printf("  %s  %s\n\n", green.Sprint("✓"), white.Sprint("Site created successfully"))

	details := [][]string{
		{"Domain", blue.Sprint("https://" + domain)},
		{"PHP", green.Sprint("8.2.27")},
		{"Document Root", dim.Sprint("~/sites/" + strings.Split(domain, ".")[0])},
		{"Config", dim.Sprint("~/.devforge/sites/" + domain + ".toml")},
		{"SSL", green.Sprint("✓ Trusted") + dim.Sprint(" (mkcert)")},
	}
	for _, d := range details {
		fmt.Printf("  %-18s%s\n", white.Sprint(d[0]), d[1])
	}

	printDivider(55)
	fmt.Printf("\n  %s %s %s\n\n",
		dim.Sprint("Open in browser:"),
		green.Sprint("devforge site:open"),
		dim.Sprint(domain),
	)
}

// ─── devforge php:list ───────────────────────────────────────────────────────

func cmdPhpList() {
	fmt.Printf("\n  %s\n\n", white.Sprint("Installed PHP versions"))

	table := tablewriter.NewWriter(os.Stdout)
	table.SetHeader([]string{"VERSION", "STATUS", "FPM PORT", "SITES", "PATH"})
	table.SetBorder(false)
	table.SetColumnSeparator("  ")
	table.SetHeaderLine(true)
	table.SetHeaderAlignment(tablewriter.ALIGN_LEFT)
	table.SetAlignment(tablewriter.ALIGN_LEFT)
	table.SetTablePadding("  ")
	table.SetNoWhiteSpace(true)
	table.SetHeaderColor(
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
	)

	rows := [][]string{
		{
			white.Sprint("8.3.6"),
			statusIcon("running"),
			dim.Sprint("9083"),
			"2",
			dim.Sprint("~/.devforge/bin/php-8.3/bin/php"),
		},
		{
			green.Sprint("8.2.27 ●"),
			statusIcon("running"),
			dim.Sprint("9082"),
			"5",
			dim.Sprint("~/.devforge/bin/php-8.2/bin/php"),
		},
		{
			white.Sprint("8.1.28"),
			statusIcon("running"),
			dim.Sprint("9081"),
			"1",
			dim.Sprint("~/.devforge/bin/php-8.1/bin/php"),
		},
		{
			dim.Sprint("8.0.30"),
			statusIcon("stopped"),
			dim.Sprint("9080"),
			"0",
			dim.Sprint("~/.devforge/bin/php-8.0/bin/php"),
		},
		{
			dim.Sprint("7.4.33"),
			statusIcon("stopped"),
			dim.Sprint("9074"),
			"1",
			dim.Sprint("~/.devforge/bin/php-7.4/bin/php"),
		},
	}
	for _, r := range rows {
		table.Append(r)
	}
	table.Render()

	fmt.Printf("\n  %s\n", dim.Sprint("5 installed  ·  3 running  ·  ● 8.2.27 is default"))
	fmt.Printf("  %s %s\n",
		dim.Sprint("Available to install:"),
		yellow.Sprint("8.4 (RC2)"),
	)
	hint("Run 'devforge php:install 8.4' to install")
	fmt.Println()
}

// ─── devforge php:install ────────────────────────────────────────────────────

func cmdPhpInstall(version string) {
	fmt.Printf("\n  %s %s\n\n",
		white.Sprint("Installing PHP"),
		magenta.Sprint(version),
	)

	filename := fmt.Sprintf("php-%s-win32-vs17-x64.zip", version)
	sizeMB := 28.4

	fmt.Printf("  %s %s %s %s\n",
		dim.Sprintf("[1/4]"),
		white.Sprint("Downloading"),
		blue.Sprint(filename),
		dim.Sprintf("(%.1f MB) ...", sizeMB),
	)
	fmt.Println()

	bar := progressbar.NewOptions(100,
		progressbar.OptionSetWriter(os.Stdout),
		progressbar.OptionSetWidth(44),
		progressbar.OptionSetPredictTime(false),
		progressbar.OptionSetDescription("  "),
		progressbar.OptionSetTheme(progressbar.Theme{
			Saucer:        "█",
			SaucerHead:    "█",
			SaucerPadding: "░",
			BarStart:      "",
			BarEnd:        "",
		}),
		progressbar.OptionOnCompletion(func() { fmt.Println() }),
		progressbar.OptionShowBytes(false),
		progressbar.OptionSpinnerType(14),
	)

	for i := 0; i < 100; i++ {
		bar.Add(1)
		sleep(25 + rand.Intn(15))
	}

	fmt.Println()

	remainingSteps := []string{
		"Verifying checksum (SHA-256) ...",
		fmt.Sprintf("Extracting to ~/.devforge/bin/php-%s/ ...", version),
		"Configuring PHP-FPM ...",
	}
	for i, s := range remainingSteps {
		stepLine(i+2, 4, s)
		sleep(300 + rand.Intn(200))
	}

	printDivider(55)
	fmt.Printf("  %s  %s\n\n", green.Sprint("✓"), white.Sprint("PHP "+version+" installed"))

	details := [][]string{
		{"Extensions", dim.Sprint("bcmath, curl, gd, intl, mbstring, mysqli,\n" +
			"                    openssl, pdo_mysql, redis, zip")},
		{"FPM port", dim.Sprint("9084")},
		{"Path", dim.Sprint("~/.devforge/bin/php-" + version + "/bin/php")},
	}
	for _, d := range details {
		fmt.Printf("  %-18s%s\n", white.Sprint(d[0]), d[1])
	}

	printDivider(55)
	fmt.Printf("\n  %s %s\n\n",
		dim.Sprint("Run 'devforge php:use "+version+"' to set as default"),
		"",
	)
}

// ─── devforge ssl:status ─────────────────────────────────────────────────────

func cmdSSLStatus() {
	fmt.Printf("\n  %s\n\n", white.Sprint("SSL Certificates"))

	table := tablewriter.NewWriter(os.Stdout)
	table.SetHeader([]string{"DOMAIN", "ISSUER", "EXPIRES", "STATUS"})
	table.SetBorder(false)
	table.SetColumnSeparator("  ")
	table.SetHeaderLine(true)
	table.SetHeaderAlignment(tablewriter.ALIGN_LEFT)
	table.SetAlignment(tablewriter.ALIGN_LEFT)
	table.SetTablePadding("  ")
	table.SetNoWhiteSpace(true)
	table.SetHeaderColor(
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
	)

	rows := [][]string{
		{"myapp.test", dim.Sprint("DevForge CA"), dim.Sprint("2027-04-09"), green.Sprint("✓ valid") + dim.Sprint(" (366 days)")},
		{"api.local", dim.Sprint("DevForge CA"), dim.Sprint("2027-04-09"), green.Sprint("✓ valid") + dim.Sprint(" (366 days)")},
		{"legacy.test", dim.Sprint("—"), dim.Sprint("—"), red.Sprint("✗ no cert")},
	}
	for _, r := range rows {
		table.Append(r)
	}
	table.Render()

	fmt.Printf("\n  %-14s%s\n", white.Sprint("CA Status:"), green.Sprint("✓ DevForge Local CA installed and trusted"))
	fmt.Printf("  %-14s%s\n", white.Sprint("CA Cert:"), dim.Sprint("~/.devforge/ca/rootCA.pem"))
	fmt.Printf("  %-14s%s\n", white.Sprint("Cert Store:"), dim.Sprint("Windows Certificate Store"))
	hint("Use 'devforge ssl:renew <domain>' to renew a certificate")
	fmt.Println()
}

// ─── devforge db:list ────────────────────────────────────────────────────────

func cmdDBList() {
	fmt.Printf("\n  %s %s\n\n",
		white.Sprint("Databases"),
		dim.Sprint("(MySQL 8.0 · 127.0.0.1:3306)"),
	)

	table := tablewriter.NewWriter(os.Stdout)
	table.SetHeader([]string{"NAME", "SIZE", "TABLES", "LINKED SITE", "MODIFIED"})
	table.SetBorder(false)
	table.SetColumnSeparator("  ")
	table.SetHeaderLine(true)
	table.SetHeaderAlignment(tablewriter.ALIGN_LEFT)
	table.SetAlignment(tablewriter.ALIGN_LEFT)
	table.SetTablePadding("  ")
	table.SetNoWhiteSpace(true)
	table.SetHeaderColor(
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
		tablewriter.Colors{tablewriter.Bold},
	)

	rows := [][]string{
		{white.Sprint("myapp"), "12.4 MB", "18", blue.Sprint("myapp.test"), dim.Sprint("2026-04-08 14:22")},
		{white.Sprint("api_development"), "4.1 MB", "9", blue.Sprint("api.local"), dim.Sprint("2026-04-07 11:01")},
		{white.Sprint("legacy_db"), "98.2 MB", "47", blue.Sprint("legacy.test"), dim.Sprint("2025-12-11 09:44")},
		{white.Sprint("wordpress_test"), "8.3 MB", "12", dim.Sprint("—"), dim.Sprint("2026-03-20 16:30")},
	}
	for _, r := range rows {
		table.Append(r)
	}
	table.Render()

	fmt.Printf("\n  %s\n", dim.Sprint("4 databases  ·  total 123.0 MB"))
	hint("'devforge db:create <name>'  ·  'devforge db:open <name>'")
	fmt.Println()
}

// ─── devforge doctor ─────────────────────────────────────────────────────────

func cmdDoctor() {
	fmt.Printf("\n  %s\n\n", white.Sprint("DevForge Doctor  —  System Diagnostics"))

	type check struct {
		category string
		label    string
		status   string // ok, warn, error
		detail   string
	}

	checks := []check{
		// CORE
		{category: "CORE", label: "devforge daemon", status: "ok", detail: "running (v1.0.0, pid 1024)"},
		{category: "", label: "devforge config", status: "ok", detail: "valid (~/.devforge/config.toml)"},
		{category: "", label: "socket", status: "ok", detail: "/tmp/devforge.sock  accessible"},
		// SERVICES
		{category: "SERVICES", label: "Apache 2.4.58", status: "ok", detail: "/usr/local/opt/httpd/bin/httpd"},
		{category: "", label: "MySQL 8.0.36", status: "ok", detail: "/usr/local/opt/mysql/bin/mysqld"},
		{category: "", label: "PHP-FPM 7.4", status: "warn", detail: "config warning: pm.max_children low (1)"},
		{category: "", label: "mkcert 1.4.4", status: "ok", detail: "CA installed and trusted"},
		// NETWORK
		{category: "NETWORK", label: "Port 80", status: "ok", detail: "free / bound by devforge"},
		{category: "", label: "Port 443", status: "ok", detail: "free / bound by devforge"},
		{category: "", label: "Port 3306", status: "error", detail: "bound by external process (pid 991)"},
		{category: "", label: "DNS resolution", status: "ok", detail: "myapp.test → 127.0.0.1  ✓"},
		// FILESYSTEM
		{category: "FILESYSTEM", label: "Config dir", status: "ok", detail: "~/.devforge/  (rw)"},
		{category: "", label: "Log dir", status: "ok", detail: "~/.devforge/logs/  (rw)"},
		{category: "", label: "Bin dir", status: "ok", detail: "~/.devforge/bin/  (rw)"},
		{category: "", label: "/etc/hosts", status: "error", detail: "not writable (run: devforge fix:hosts)"},
	}

	lastCategory := ""
	for _, c := range checks {
		if c.category != "" && c.category != lastCategory {
			fmt.Printf("  %s\n", white.Sprint(c.category))
			lastCategory = c.category
		}
		var icon string
		switch c.status {
		case "ok":
			icon = green.Sprint("✓")
		case "warn":
			icon = yellow.Sprint("⚡")
		case "error":
			icon = red.Sprint("✗")
		}
		fmt.Printf("  %s  %-22s%s\n", icon, white.Sprint(c.label), dim.Sprint(c.detail))
	}

	printDivider(55)
	fmt.Printf("\n  %s\n", yellow.Sprint("2 issues found"))
	fmt.Printf("\n  %s\n\n",
		dim.Sprint("Run 'devforge doctor --fix' to automatically resolve issues"),
	)
}

// ─── devforge start ───────────────────────────────────────────────────────────

func cmdStart() {
	fmt.Printf("\n  %s\n\n", white.Sprint("Starting DevForge services"))

	type svc struct {
		name    string
		skip    bool
		skipMsg string
		pid     int
	}

	services := []svc{
		{name: "Apache 2.4.58", pid: 14220},
		{name: "PHP-FPM 8.2", pid: 14224},
		{name: "PHP-FPM 8.3", pid: 14228},
		{name: "PHP-FPM 8.1", skip: true, skipMsg: "disabled"},
		{name: "MySQL 8.0", pid: 14235},
		{name: "Redis 7.2", pid: 14240},
		{name: "Mailpit 1.14", pid: 14245},
	}

	start := time.Now()
	for _, s := range services {
		label := fmt.Sprintf("%-20s", s.name)
		if s.skip {
			fmt.Printf("  %s  %s  %s  %s\n",
				dim.Sprint("○"),
				white.Sprint(label),
				dim.Sprint("skipped"),
				dim.Sprintf("(%s)", s.skipMsg),
			)
			continue
		}
		sleep(150 + rand.Intn(200))
		fmt.Printf("  %s  %s  %s  %s\n",
			green.Sprint("✓"),
			white.Sprint(label),
			green.Sprint("started"),
			dim.Sprintf("(pid %d)", s.pid),
		)
	}

	elapsed := time.Since(start)
	fmt.Printf("\n  %s %s\n\n",
		dim.Sprint("All services started in"),
		green.Sprintf("%.1fs", elapsed.Seconds()),
	)
}

// ─── devforge stop <service> ─────────────────────────────────────────────────

func cmdStop(service string) {
	fmt.Printf("\n  %s %s ...", white.Sprint("Stopping"), magenta.Sprint(service))
	sleep(400)
	fmt.Printf("  %s\n\n",
		green.Sprint("✓  stopped  ")+dim.Sprint("(was pid 12847)"),
	)
}

// ─── usage / dispatch ────────────────────────────────────────────────────────

func printUsage() {
	fmt.Printf("\n  %s  %s\n\n", magenta.Sprint("DevForge"), dim.Sprint("v1.0.0"))
	fmt.Printf("  %s\n\n", white.Sprint("USAGE"))
	fmt.Printf("    %s\n\n", dim.Sprint("devforge <command> [arguments] [flags]"))
	fmt.Printf("  %s\n\n", white.Sprint("COMMANDS"))

	cmds := [][2]string{
		{"status", "Show service status overview"},
		{"start [service]", "Start all or a named service"},
		{"stop <service>", "Stop a service"},
		{"restart <service>", "Restart a service"},
		{"site:list", "List all configured sites"},
		{"site:create <domain>", "Create a new site (interactive if no args)"},
		{"site:delete <domain>", "Delete a site"},
		{"site:info <domain>", "Show site details"},
		{"php:list", "List installed PHP versions"},
		{"php:install <version>", "Install a PHP version"},
		{"php:use <version>", "Set default PHP version"},
		{"ssl:status", "Show SSL certificate status"},
		{"db:list", "List all databases"},
		{"db:create <name>", "Create a database"},
		{"doctor", "Run system diagnostics"},
	}

	for _, c := range cmds {
		fmt.Printf("    %-30s%s\n", green.Sprint(c[0]), dim.Sprint(c[1]))
	}

	fmt.Printf("\n  %s\n\n", white.Sprint("FLAGS"))
	fmt.Printf("    %-30s%s\n", blue.Sprint("--json"), dim.Sprint("Output as JSON"))
	fmt.Printf("    %-30s%s\n", blue.Sprint("--quiet, -q"), dim.Sprint("Suppress all output except errors"))
	fmt.Printf("    %-30s%s\n", blue.Sprint("--no-color"), dim.Sprint("Disable ANSI color output"))
	fmt.Printf("    %-30s%s\n", blue.Sprint("--help, -h"), dim.Sprint("Show command help"))
	fmt.Printf("\n  %s %s\n\n",
		dim.Sprint("Run"),
		dim.Sprint("devforge <command> --help  for more information about a command."),
	)
}

func main() {
	rand.New(rand.NewSource(time.Now().UnixNano()))

	args := os.Args[1:]
	if len(args) == 0 {
		printUsage()
		return
	}

	cmd := args[0]
	switch cmd {
	case "status":
		cmdStatus()
	case "site:list":
		cmdSiteList()
	case "site:create":
		domain := "myapp.test"
		if len(args) > 1 {
			domain = args[1]
		}
		cmdSiteCreate(domain)
	case "php:list":
		cmdPhpList()
	case "php:install":
		version := "8.4"
		if len(args) > 1 {
			version = args[1]
		}
		cmdPhpInstall(version)
	case "ssl:status":
		cmdSSLStatus()
	case "db:list":
		cmdDBList()
	case "doctor":
		cmdDoctor()
	case "start":
		cmdStart()
	case "stop":
		svc := "apache"
		if len(args) > 1 {
			svc = args[1]
		}
		cmdStop(svc)
	default:
		fmt.Printf("\n  %s %s\n\n",
			red.Sprint("✗  Unknown command:"),
			white.Sprint(cmd),
		)
		fmt.Printf("  %s %s\n\n",
			dim.Sprint("Run"),
			dim.Sprint("devforge --help  for a list of commands."),
		)
		os.Exit(1)
	}
}
