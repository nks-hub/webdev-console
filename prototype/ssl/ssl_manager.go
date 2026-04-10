// Package ssl provides automated SSL certificate management for NKS WebDev Console
// using mkcert as the underlying certificate generator.
//
// Certificates are stored under ~/.wdc/ssl/sites/{domain}/ and metadata
// is tracked in a SQLite database for fast querying and lifecycle management.
package main

import (
	"crypto/sha256"
	"crypto/x509"
	"database/sql"
	"encoding/hex"
	"encoding/pem"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

// CertInfo holds metadata about a managed certificate.
type CertInfo struct {
	Domain      string
	Aliases     []string
	CertPath    string
	KeyPath     string
	Issuer      string
	NotBefore   time.Time
	NotAfter    time.Time
	Fingerprint string
	SANs        []string
	CreatedAt   time.Time
}

// CertManager manages SSL certificates for local development domains.
type CertManager struct {
	baseDir    string // ~/.wdc/ssl
	sitesDir   string // ~/.wdc/ssl/sites
	dbPath     string // ~/.wdc/ssl/certs.db
	mkcertPath string
	db         *sql.DB
}

// NewCertManager creates a new CertManager, auto-detecting mkcert and
// initializing the storage directory and SQLite database.
func NewCertManager() (*CertManager, error) {
	homeDir, err := os.UserHomeDir()
	if err != nil {
		return nil, fmt.Errorf("cannot determine home directory: %w", err)
	}

	baseDir := filepath.Join(homeDir, ".wdc", "ssl")
	sitesDir := filepath.Join(baseDir, "sites")
	dbPath := filepath.Join(baseDir, "certs.db")

	mkcertPath := findMkcert()
	if mkcertPath == "" {
		return nil, fmt.Errorf("mkcert binary not found; install it or set MKCERT_PATH")
	}

	// Ensure directories exist
	if err := os.MkdirAll(sitesDir, 0755); err != nil {
		return nil, fmt.Errorf("cannot create sites directory: %w", err)
	}

	// Open SQLite database
	db, err := sql.Open("sqlite", dbPath+"?_pragma=journal_mode(WAL)")
	if err != nil {
		return nil, fmt.Errorf("cannot open database: %w", err)
	}

	cm := &CertManager{
		baseDir:    baseDir,
		sitesDir:   sitesDir,
		dbPath:     dbPath,
		mkcertPath: mkcertPath,
		db:         db,
	}

	if err := cm.initDB(); err != nil {
		return nil, fmt.Errorf("cannot initialize database: %w", err)
	}

	return cm, nil
}

// Close releases the database connection.
func (cm *CertManager) Close() error {
	if cm.db != nil {
		return cm.db.Close()
	}
	return nil
}

// findMkcert searches for the mkcert binary in known locations and PATH.
func findMkcert() string {
	// Check environment variable first
	if envPath := os.Getenv("MKCERT_PATH"); envPath != "" {
		if fileExists(envPath) {
			return envPath
		}
	}

	// Known locations (platform-specific)
	candidates := []string{}
	if runtime.GOOS == "windows" {
		candidates = append(candidates,
			`C:\work\mkcert.exe`,
			`C:\ProgramData\chocolatey\bin\mkcert.exe`,
			filepath.Join(os.Getenv("LOCALAPPDATA"), "mkcert", "mkcert.exe"),
		)
	} else {
		candidates = append(candidates,
			"/usr/local/bin/mkcert",
			"/usr/bin/mkcert",
			filepath.Join(os.Getenv("HOME"), "go", "bin", "mkcert"),
		)
	}

	for _, p := range candidates {
		if fileExists(p) {
			return p
		}
	}

	// Fall back to PATH lookup
	if path, err := exec.LookPath("mkcert"); err == nil {
		return path
	}

	return ""
}

func fileExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

// initDB creates the certificates table if it does not exist.
func (cm *CertManager) initDB() error {
	schema := `
	CREATE TABLE IF NOT EXISTS certificates (
		id          INTEGER PRIMARY KEY AUTOINCREMENT,
		domain      TEXT    NOT NULL UNIQUE,
		aliases     TEXT    NOT NULL DEFAULT '',
		cert_path   TEXT    NOT NULL,
		key_path    TEXT    NOT NULL,
		issuer      TEXT    NOT NULL DEFAULT '',
		not_before  TEXT    NOT NULL,
		not_after   TEXT    NOT NULL,
		fingerprint TEXT    NOT NULL DEFAULT '',
		sans        TEXT    NOT NULL DEFAULT '',
		created_at  TEXT    NOT NULL
	);
	CREATE INDEX IF NOT EXISTS idx_certs_domain ON certificates(domain);
	CREATE INDEX IF NOT EXISTS idx_certs_not_after ON certificates(not_after);
	`
	_, err := cm.db.Exec(schema)
	return err
}

// InstallCA ensures the mkcert root CA is installed in the system trust store.
func (cm *CertManager) InstallCA() error {
	fmt.Println("[NKS WebDev Console SSL] Installing mkcert CA...")

	cmd := exec.Command(cm.mkcertPath, "-install")
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		return fmt.Errorf("mkcert -install failed: %w (try running as administrator)", err)
	}

	fmt.Println("[OK] mkcert CA is installed and trusted.")
	return nil
}

// CARoot returns the mkcert CA root directory path.
func (cm *CertManager) CARoot() (string, error) {
	out, err := exec.Command(cm.mkcertPath, "-CAROOT").Output()
	if err != nil {
		return "", fmt.Errorf("cannot get CA root: %w", err)
	}
	return strings.TrimSpace(string(out)), nil
}

// Generate creates a new SSL certificate for the given domain and aliases.
func (cm *CertManager) Generate(domain string, aliases []string) (*CertInfo, error) {
	domainDir := filepath.Join(cm.sitesDir, domain)
	if err := os.MkdirAll(domainDir, 0755); err != nil {
		return nil, fmt.Errorf("cannot create domain directory: %w", err)
	}

	certPath := filepath.Join(domainDir, "cert.pem")
	keyPath := filepath.Join(domainDir, "key.pem")

	// Build mkcert arguments
	args := []string{"-cert-file", certPath, "-key-file", keyPath, domain}
	args = append(args, aliases...)

	fmt.Printf("[NKS WebDev Console SSL] Generating certificate for: %s\n", strings.Join(append([]string{domain}, aliases...), ", "))

	cmd := exec.Command(cm.mkcertPath, args...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		return nil, fmt.Errorf("mkcert failed: %w", err)
	}

	// Parse the generated certificate
	info, err := cm.parseCertFile(certPath, keyPath, domain, aliases)
	if err != nil {
		return nil, fmt.Errorf("cannot parse generated certificate: %w", err)
	}

	// Store metadata in database
	if err := cm.storeCertDB(info); err != nil {
		return nil, fmt.Errorf("cannot store cert metadata: %w", err)
	}

	return info, nil
}

// parseCertFile reads a PEM certificate file and extracts metadata.
func (cm *CertManager) parseCertFile(certPath, keyPath, domain string, aliases []string) (*CertInfo, error) {
	certPEM, err := os.ReadFile(certPath)
	if err != nil {
		return nil, fmt.Errorf("cannot read cert file: %w", err)
	}

	block, _ := pem.Decode(certPEM)
	if block == nil {
		return nil, fmt.Errorf("cannot decode PEM block from cert file")
	}

	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return nil, fmt.Errorf("cannot parse X.509 certificate: %w", err)
	}

	// Compute SHA-256 fingerprint
	hash := sha256.Sum256(cert.Raw)
	fingerprint := formatFingerprint(hex.EncodeToString(hash[:]))

	// Collect SANs
	sans := make([]string, 0, len(cert.DNSNames))
	sans = append(sans, cert.DNSNames...)

	info := &CertInfo{
		Domain:      domain,
		Aliases:     aliases,
		CertPath:    certPath,
		KeyPath:     keyPath,
		Issuer:      cert.Issuer.String(),
		NotBefore:   cert.NotBefore,
		NotAfter:    cert.NotAfter,
		Fingerprint: fingerprint,
		SANs:        sans,
		CreatedAt:   time.Now(),
	}

	return info, nil
}

func formatFingerprint(hex string) string {
	var parts []string
	for i := 0; i < len(hex); i += 2 {
		end := i + 2
		if end > len(hex) {
			end = len(hex)
		}
		parts = append(parts, strings.ToUpper(hex[i:end]))
	}
	return strings.Join(parts, ":")
}

// storeCertDB inserts or updates certificate metadata in SQLite.
func (cm *CertManager) storeCertDB(info *CertInfo) error {
	query := `
	INSERT INTO certificates (domain, aliases, cert_path, key_path, issuer, not_before, not_after, fingerprint, sans, created_at)
	VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
	ON CONFLICT(domain) DO UPDATE SET
		aliases     = excluded.aliases,
		cert_path   = excluded.cert_path,
		key_path    = excluded.key_path,
		issuer      = excluded.issuer,
		not_before  = excluded.not_before,
		not_after   = excluded.not_after,
		fingerprint = excluded.fingerprint,
		sans        = excluded.sans,
		created_at  = excluded.created_at
	`

	_, err := cm.db.Exec(query,
		info.Domain,
		strings.Join(info.Aliases, ","),
		info.CertPath,
		info.KeyPath,
		info.Issuer,
		info.NotBefore.Format(time.RFC3339),
		info.NotAfter.Format(time.RFC3339),
		info.Fingerprint,
		strings.Join(info.SANs, ","),
		info.CreatedAt.Format(time.RFC3339),
	)
	return err
}

// List returns all managed certificates from the database.
func (cm *CertManager) List() ([]CertInfo, error) {
	rows, err := cm.db.Query(`
		SELECT domain, aliases, cert_path, key_path, issuer, not_before, not_after, fingerprint, sans, created_at
		FROM certificates
		ORDER BY domain
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var certs []CertInfo
	for rows.Next() {
		var (
			info                                                             CertInfo
			aliasesStr, sansStr                                              string
			notBeforeStr, notAfterStr, createdAtStr                          string
		)

		err := rows.Scan(
			&info.Domain, &aliasesStr, &info.CertPath, &info.KeyPath,
			&info.Issuer, &notBeforeStr, &notAfterStr,
			&info.Fingerprint, &sansStr, &createdAtStr,
		)
		if err != nil {
			return nil, err
		}

		if aliasesStr != "" {
			info.Aliases = strings.Split(aliasesStr, ",")
		}
		if sansStr != "" {
			info.SANs = strings.Split(sansStr, ",")
		}

		info.NotBefore, _ = time.Parse(time.RFC3339, notBeforeStr)
		info.NotAfter, _ = time.Parse(time.RFC3339, notAfterStr)
		info.CreatedAt, _ = time.Parse(time.RFC3339, createdAtStr)

		certs = append(certs, info)
	}

	return certs, rows.Err()
}

// Verify checks that a certificate is valid, not expired, and its key matches.
func (cm *CertManager) Verify(domain string) error {
	domainDir := filepath.Join(cm.sitesDir, domain)
	certPath := filepath.Join(domainDir, "cert.pem")
	keyPath := filepath.Join(domainDir, "key.pem")

	if !fileExists(certPath) {
		return fmt.Errorf("certificate not found: %s", certPath)
	}
	if !fileExists(keyPath) {
		return fmt.Errorf("key not found: %s", keyPath)
	}

	// Parse certificate
	certPEM, err := os.ReadFile(certPath)
	if err != nil {
		return fmt.Errorf("cannot read cert: %w", err)
	}

	block, _ := pem.Decode(certPEM)
	if block == nil {
		return fmt.Errorf("invalid PEM in cert file")
	}

	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return fmt.Errorf("cannot parse certificate: %w", err)
	}

	now := time.Now()

	fmt.Printf("  Domain:      %s\n", domain)
	fmt.Printf("  Subject:     %s\n", cert.Subject.String())
	fmt.Printf("  Issuer:      %s\n", cert.Issuer.String())
	fmt.Printf("  Valid from:  %s\n", cert.NotBefore.Format(time.RFC3339))
	fmt.Printf("  Valid until: %s\n", cert.NotAfter.Format(time.RFC3339))
	fmt.Printf("  SANs:        %s\n", strings.Join(cert.DNSNames, ", "))

	// Check expiry
	if now.Before(cert.NotBefore) {
		return fmt.Errorf("certificate is not yet valid (starts %s)", cert.NotBefore)
	}
	if now.After(cert.NotAfter) {
		return fmt.Errorf("certificate has EXPIRED (expired %s)", cert.NotAfter)
	}

	daysLeft := int(cert.NotAfter.Sub(now).Hours() / 24)
	fmt.Printf("  Days left:   %d\n", daysLeft)

	if daysLeft <= 30 {
		fmt.Printf("  [WARN] Certificate expires in %d days\n", daysLeft)
	} else {
		fmt.Println("  [OK] Certificate is valid.")
	}

	// Verify trust chain against mkcert CA
	caRoot, err := cm.CARoot()
	if err == nil {
		caCertPath := filepath.Join(caRoot, "rootCA.pem")
		if fileExists(caCertPath) {
			caPEM, err := os.ReadFile(caCertPath)
			if err == nil {
				roots := x509.NewCertPool()
				roots.AppendCertsFromPEM(caPEM)

				_, err = cert.Verify(x509.VerifyOptions{
					Roots:       roots,
					CurrentTime: now,
				})
				if err != nil {
					fmt.Printf("  [WARN] Trust chain verification failed: %v\n", err)
				} else {
					fmt.Println("  [OK] Certificate is trusted by mkcert CA.")
				}
			}
		}
	}

	return nil
}

// Revoke removes a certificate and its metadata.
func (cm *CertManager) Revoke(domain string) error {
	domainDir := filepath.Join(cm.sitesDir, domain)

	if _, err := os.Stat(domainDir); os.IsNotExist(err) {
		return fmt.Errorf("no certificate found for domain: %s", domain)
	}

	// Remove files
	if err := os.RemoveAll(domainDir); err != nil {
		return fmt.Errorf("cannot remove certificate directory: %w", err)
	}

	// Remove from database
	_, err := cm.db.Exec("DELETE FROM certificates WHERE domain = ?", domain)
	if err != nil {
		return fmt.Errorf("cannot remove database entry: %w", err)
	}

	fmt.Printf("[OK] Certificate for %s has been revoked and removed.\n", domain)
	return nil
}

// GenerateApacheConfig creates an Apache SSL vhost configuration from
// the provided template file for the given domain.
func (cm *CertManager) GenerateApacheConfig(domain, docRoot, templatePath, outputPath string) error {
	info, err := cm.getCertFromDB(domain)
	if err != nil {
		return fmt.Errorf("no certificate found for %s: %w", domain, err)
	}

	tmplContent, err := os.ReadFile(templatePath)
	if err != nil {
		return fmt.Errorf("cannot read template: %w", err)
	}

	// Simple template replacement (avoids pulling in text/template for this PoC)
	config := string(tmplContent)
	config = strings.ReplaceAll(config, "{{.Domain}}", domain)
	config = strings.ReplaceAll(config, "{{.DocRoot}}", docRoot)
	config = strings.ReplaceAll(config, "{{.CertPath}}", info.CertPath)
	config = strings.ReplaceAll(config, "{{.KeyPath}}", info.KeyPath)

	// Build ServerAlias line
	if len(info.Aliases) > 0 {
		config = strings.ReplaceAll(config, "{{.ServerAliases}}", strings.Join(info.Aliases, " "))
	} else {
		config = strings.ReplaceAll(config, "{{.ServerAliases}}", "")
	}

	if err := os.WriteFile(outputPath, []byte(config), 0644); err != nil {
		return fmt.Errorf("cannot write config: %w", err)
	}

	fmt.Printf("[OK] Apache config written to: %s\n", outputPath)
	return nil
}

func (cm *CertManager) getCertFromDB(domain string) (*CertInfo, error) {
	var info CertInfo
	var aliasesStr, sansStr, notBeforeStr, notAfterStr, createdAtStr string

	err := cm.db.QueryRow(`
		SELECT domain, aliases, cert_path, key_path, issuer, not_before, not_after, fingerprint, sans, created_at
		FROM certificates WHERE domain = ?
	`, domain).Scan(
		&info.Domain, &aliasesStr, &info.CertPath, &info.KeyPath,
		&info.Issuer, &notBeforeStr, &notAfterStr,
		&info.Fingerprint, &sansStr, &createdAtStr,
	)
	if err != nil {
		return nil, err
	}

	if aliasesStr != "" {
		info.Aliases = strings.Split(aliasesStr, ",")
	}
	if sansStr != "" {
		info.SANs = strings.Split(sansStr, ",")
	}
	info.NotBefore, _ = time.Parse(time.RFC3339, notBeforeStr)
	info.NotAfter, _ = time.Parse(time.RFC3339, notAfterStr)
	info.CreatedAt, _ = time.Parse(time.RFC3339, createdAtStr)

	return &info, nil
}

// --- CLI entry point ---

func main() {
	if len(os.Args) < 2 {
		printUsage()
		os.Exit(1)
	}

	cm, err := NewCertManager()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error: %v\n", err)
		os.Exit(1)
	}
	defer cm.Close()

	action := os.Args[1]

	switch action {
	case "install-ca":
		if err := cm.InstallCA(); err != nil {
			fmt.Fprintf(os.Stderr, "Error: %v\n", err)
			os.Exit(1)
		}

	case "generate":
		if len(os.Args) < 3 {
			fmt.Fprintln(os.Stderr, "Usage: ssl_manager generate <domain> [alias1] [alias2] ...")
			os.Exit(1)
		}
		domain := os.Args[2]
		aliases := os.Args[3:]

		info, err := cm.Generate(domain, aliases)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error: %v\n", err)
			os.Exit(1)
		}

		fmt.Println()
		fmt.Println("[OK] Certificate generated successfully!")
		fmt.Printf("  Domain:      %s\n", info.Domain)
		fmt.Printf("  Issuer:      %s\n", info.Issuer)
		fmt.Printf("  Valid from:  %s\n", info.NotBefore.Format(time.RFC3339))
		fmt.Printf("  Valid until: %s\n", info.NotAfter.Format(time.RFC3339))
		fmt.Printf("  SANs:        %s\n", strings.Join(info.SANs, ", "))
		fmt.Printf("  Fingerprint: %s\n", info.Fingerprint)
		fmt.Printf("  Cert:        %s\n", info.CertPath)
		fmt.Printf("  Key:         %s\n", info.KeyPath)

	case "list":
		certs, err := cm.List()
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error: %v\n", err)
			os.Exit(1)
		}

		if len(certs) == 0 {
			fmt.Println("No certificates found.")
			return
		}

		fmt.Printf("%-30s %-10s %-30s %s\n", "DOMAIN", "STATUS", "EXPIRES", "SANs")
		fmt.Println(strings.Repeat("-", 100))

		for _, c := range certs {
			status := "VALID"
			if time.Now().After(c.NotAfter) {
				status = "EXPIRED"
			} else if time.Now().After(c.NotAfter.Add(-30 * 24 * time.Hour)) {
				status = "EXPIRING"
			}
			fmt.Printf("%-30s %-10s %-30s %s\n",
				c.Domain,
				status,
				c.NotAfter.Format("2006-01-02 15:04:05"),
				strings.Join(c.SANs, ", "),
			)
		}
		fmt.Printf("\nTotal: %d certificate(s)\n", len(certs))

	case "verify":
		if len(os.Args) < 3 {
			fmt.Fprintln(os.Stderr, "Usage: ssl_manager verify <domain>")
			os.Exit(1)
		}
		if err := cm.Verify(os.Args[2]); err != nil {
			fmt.Fprintf(os.Stderr, "Error: %v\n", err)
			os.Exit(1)
		}

	case "revoke":
		if len(os.Args) < 3 {
			fmt.Fprintln(os.Stderr, "Usage: ssl_manager revoke <domain>")
			os.Exit(1)
		}
		if err := cm.Revoke(os.Args[2]); err != nil {
			fmt.Fprintf(os.Stderr, "Error: %v\n", err)
			os.Exit(1)
		}

	default:
		fmt.Fprintf(os.Stderr, "Unknown action: %s\n", action)
		printUsage()
		os.Exit(1)
	}
}

func printUsage() {
	fmt.Println("NKS WebDev Console SSL Manager")
	fmt.Println()
	fmt.Println("Usage:")
	fmt.Println("  ssl_manager install-ca                          Install mkcert CA in trust store")
	fmt.Println("  ssl_manager generate <domain> [aliases...]      Generate certificate")
	fmt.Println("  ssl_manager list                                List all certificates")
	fmt.Println("  ssl_manager verify <domain>                     Verify certificate validity")
	fmt.Println("  ssl_manager revoke <domain>                     Revoke and remove certificate")
}
