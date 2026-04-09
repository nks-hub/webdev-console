package config

import (
	"strings"
	"testing"
)

const validTOML = `
[site]
hostname = "myapp.test"
aliases = ["www.myapp.test"]
document_root = "C:\\work\\sites\\myapp\\www"

[php]
version = "8.2"

[ssl]
enabled = true

[server]
type = "apache"
`

const minimalTOML = `
[site]
hostname = "minimal.test"
document_root = "C:\\sites\\minimal"
`

func TestParseBytes_Valid(t *testing.T) {
	cfg, err := ParseBytes([]byte(validTOML))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Site.Hostname != "myapp.test" {
		t.Errorf("hostname = %q, want %q", cfg.Site.Hostname, "myapp.test")
	}
	if len(cfg.Site.Aliases) != 1 || cfg.Site.Aliases[0] != "www.myapp.test" {
		t.Errorf("aliases = %v, want [www.myapp.test]", cfg.Site.Aliases)
	}
	if cfg.PHP.Version != "8.2" {
		t.Errorf("php.version = %q, want %q", cfg.PHP.Version, "8.2")
	}
	if !cfg.SSL.Enabled {
		t.Error("ssl.enabled should be true")
	}
	if cfg.Server.Type != "apache" {
		t.Errorf("server.type = %q, want %q", cfg.Server.Type, "apache")
	}
}

func TestParseBytes_Minimal(t *testing.T) {
	cfg, err := ParseBytes([]byte(minimalTOML))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Site.Hostname != "minimal.test" {
		t.Errorf("hostname = %q, want %q", cfg.Site.Hostname, "minimal.test")
	}
	if cfg.Server.Type != "apache" {
		t.Errorf("server.type should default to apache, got %q", cfg.Server.Type)
	}
}

func TestParseBytes_MissingHostname(t *testing.T) {
	bad := `
[site]
document_root = "C:\\sites\\x"
`
	_, err := ParseBytes([]byte(bad))
	if err == nil {
		t.Fatal("expected error for missing hostname")
	}
	if !strings.Contains(err.Error(), "hostname") {
		t.Errorf("error should mention hostname, got: %v", err)
	}
}

func TestParseBytes_MissingDocumentRoot(t *testing.T) {
	bad := `
[site]
hostname = "no-root.test"
`
	_, err := ParseBytes([]byte(bad))
	if err == nil {
		t.Fatal("expected error for missing document_root")
	}
	if !strings.Contains(err.Error(), "document_root") {
		t.Errorf("error should mention document_root, got: %v", err)
	}
}

func TestParseBytes_UnsupportedServerType(t *testing.T) {
	bad := `
[site]
hostname = "x.test"
document_root = "C:\\x"

[server]
type = "lighttpd"
`
	_, err := ParseBytes([]byte(bad))
	if err == nil {
		t.Fatal("expected error for unsupported server type")
	}
}

func TestParseBytes_UnsupportedPHPVersion(t *testing.T) {
	bad := `
[site]
hostname = "x.test"
document_root = "C:\\x"

[php]
version = "5.6"
`
	_, err := ParseBytes([]byte(bad))
	if err == nil {
		t.Fatal("expected error for unsupported php version")
	}
}

func TestToRenderContext_SSLPort(t *testing.T) {
	cfg, _ := ParseBytes([]byte(validTOML))
	ctx := ToRenderContext(cfg, "C:\\logs", "C:\\certs")
	if ctx.Port != 443 {
		t.Errorf("port = %d, want 443 for SSL site", ctx.Port)
	}
}

func TestToRenderContext_NonSSLPort(t *testing.T) {
	cfg, _ := ParseBytes([]byte(minimalTOML))
	ctx := ToRenderContext(cfg, "C:\\logs", "C:\\certs")
	if ctx.Port != 80 {
		t.Errorf("port = %d, want 80 for non-SSL site", ctx.Port)
	}
}

func TestToRenderContext_PHPFPMPort(t *testing.T) {
	cfg, _ := ParseBytes([]byte(validTOML))
	ctx := ToRenderContext(cfg, "C:\\logs", "C:\\certs")
	if ctx.PHPFPMPort != 9082 {
		t.Errorf("php-fpm port = %d, want 9082 for PHP 8.2", ctx.PHPFPMPort)
	}
}

func TestToRenderContext_DefaultCertPaths(t *testing.T) {
	cfg, _ := ParseBytes([]byte(validTOML))
	ctx := ToRenderContext(cfg, "C:\\logs", "C:\\certs")
	if !strings.Contains(ctx.SSLCertPath, "myapp.test") {
		t.Errorf("cert path %q should contain hostname", ctx.SSLCertPath)
	}
	if !strings.Contains(ctx.SSLKeyPath, "myapp.test") {
		t.Errorf("key path %q should contain hostname", ctx.SSLKeyPath)
	}
}
