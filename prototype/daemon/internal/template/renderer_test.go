package template

import (
	"strings"
	"testing"

	"github.com/nks/wdc/internal/config"
)

func newTestRenderer(t *testing.T) *Renderer {
	t.Helper()
	r, err := NewRenderer()
	if err != nil {
		t.Fatalf("NewRenderer: %v", err)
	}
	return r
}

func baseContext() *config.RenderContext {
	return &config.RenderContext{
		Hostname:     "myapp.test",
		Aliases:      []string{"www.myapp.test"},
		DocumentRoot: "C:\\work\\sites\\myapp\\www",
		Port:         443,
		SSL:          true,
		SSLCertPath:  "C:\\certs\\myapp.test.crt",
		SSLKeyPath:   "C:\\certs\\myapp.test.key",
		PHPFPMPort:   9082,
		LogDir:       "C:\\logs",
		ServerType:   "apache",
	}
}

func TestRenderApache_ContainsHostname(t *testing.T) {
	r := newTestRenderer(t)
	out, err := r.RenderApache(baseContext())
	if err != nil {
		t.Fatalf("RenderApache: %v", err)
	}
	if !strings.Contains(out, "myapp.test") {
		t.Errorf("output missing hostname:\n%s", out)
	}
}

func TestRenderApache_ContainsSSLDirectives(t *testing.T) {
	r := newTestRenderer(t)
	out, err := r.RenderApache(baseContext())
	if err != nil {
		t.Fatalf("RenderApache: %v", err)
	}
	checks := []string{
		"SSLEngine on",
		"SSLCertificateFile",
		"SSLCertificateKeyFile",
		"myapp.test.crt",
		"myapp.test.key",
	}
	for _, s := range checks {
		if !strings.Contains(out, s) {
			t.Errorf("output missing %q:\n%s", s, out)
		}
	}
}

func TestRenderApache_NoSSLWhenDisabled(t *testing.T) {
	r := newTestRenderer(t)
	ctx := baseContext()
	ctx.SSL = false
	ctx.Port = 80
	out, err := r.RenderApache(ctx)
	if err != nil {
		t.Fatalf("RenderApache: %v", err)
	}
	if strings.Contains(out, "SSLEngine") {
		t.Errorf("non-SSL output should not contain SSLEngine:\n%s", out)
	}
}

func TestRenderApache_ContainsAlias(t *testing.T) {
	r := newTestRenderer(t)
	out, err := r.RenderApache(baseContext())
	if err != nil {
		t.Fatalf("RenderApache: %v", err)
	}
	if !strings.Contains(out, "ServerAlias") || !strings.Contains(out, "www.myapp.test") {
		t.Errorf("output missing ServerAlias:\n%s", out)
	}
}

func TestRenderApache_ContainsPHPFPMPort(t *testing.T) {
	r := newTestRenderer(t)
	out, err := r.RenderApache(baseContext())
	if err != nil {
		t.Fatalf("RenderApache: %v", err)
	}
	if !strings.Contains(out, "9082") {
		t.Errorf("output missing PHP-FPM port 9082:\n%s", out)
	}
}

func TestRenderApache_ContainsDocumentRoot(t *testing.T) {
	r := newTestRenderer(t)
	out, err := r.RenderApache(baseContext())
	if err != nil {
		t.Fatalf("RenderApache: %v", err)
	}
	if !strings.Contains(out, "C:\\work\\sites\\myapp\\www") {
		t.Errorf("output missing document root:\n%s", out)
	}
}

func TestRenderNginx_BasicStructure(t *testing.T) {
	r := newTestRenderer(t)
	ctx := baseContext()
	out, err := r.RenderNginx(ctx)
	if err != nil {
		t.Fatalf("RenderNginx: %v", err)
	}
	checks := []string{
		"server {",
		"listen 443 ssl",
		"server_name myapp.test",
		"fastcgi_pass 127.0.0.1:9082",
	}
	for _, s := range checks {
		if !strings.Contains(out, s) {
			t.Errorf("nginx output missing %q:\n%s", s, out)
		}
	}
}

func TestRenderUnknownServerType(t *testing.T) {
	r := newTestRenderer(t)
	ctx := baseContext()
	ctx.ServerType = "lighttpd"
	_, err := r.Render(ctx)
	if err == nil {
		t.Fatal("expected error for unknown server type")
	}
}
