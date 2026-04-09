// Package config handles parsing and validation of site configuration files.
package config

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/BurntSushi/toml"
)

// SiteConfig represents the full configuration for a single site.
type SiteConfig struct {
	Site   SiteSection   `toml:"site"`
	PHP    PHPSection    `toml:"php"`
	SSL    SSLSection    `toml:"ssl"`
	Server ServerSection `toml:"server"`
}

// SiteSection holds the core site identity settings.
type SiteSection struct {
	Hostname     string   `toml:"hostname"`
	Aliases      []string `toml:"aliases"`
	DocumentRoot string   `toml:"document_root"`
}

// PHPSection holds PHP runtime settings.
type PHPSection struct {
	Version string `toml:"version"`
}

// SSLSection holds TLS/SSL configuration.
type SSLSection struct {
	Enabled  bool   `toml:"enabled"`
	CertPath string `toml:"cert_path"`
	KeyPath  string `toml:"key_path"`
}

// ServerSection holds the web server backend settings.
type ServerSection struct {
	Type string `toml:"type"`
}

// RenderContext is the flattened view passed to template rendering.
type RenderContext struct {
	Hostname     string
	Aliases      []string
	DocumentRoot string
	Port         int
	SSL          bool
	SSLCertPath  string
	SSLKeyPath   string
	PHPFPMPort   int
	LogDir       string
	ServerType   string
}

// phpFPMPortMap maps a PHP version string to its FPM TCP port.
var phpFPMPortMap = map[string]int{
	"7.4": 9074,
	"8.0": 9080,
	"8.1": 9081,
	"8.2": 9082,
	"8.3": 9083,
}

// ParseFile reads and parses a TOML site config file from disk.
func ParseFile(path string) (*SiteConfig, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("reading config file %q: %w", path, err)
	}
	return ParseBytes(data)
}

// ParseBytes parses TOML site config from a raw byte slice.
func ParseBytes(data []byte) (*SiteConfig, error) {
	var cfg SiteConfig
	if _, err := toml.Decode(string(data), &cfg); err != nil {
		return nil, fmt.Errorf("decoding TOML: %w", err)
	}
	if err := validate(&cfg); err != nil {
		return nil, fmt.Errorf("invalid config: %w", err)
	}
	return &cfg, nil
}

// validate performs semantic validation of the parsed config.
func validate(cfg *SiteConfig) error {
	var errs []string

	if strings.TrimSpace(cfg.Site.Hostname) == "" {
		errs = append(errs, "site.hostname is required")
	}
	if strings.TrimSpace(cfg.Site.DocumentRoot) == "" {
		errs = append(errs, "site.document_root is required")
	}
	if cfg.Server.Type == "" {
		cfg.Server.Type = "apache"
	}
	serverType := strings.ToLower(cfg.Server.Type)
	if serverType != "apache" && serverType != "nginx" {
		errs = append(errs, fmt.Sprintf("server.type %q is not supported (use apache or nginx)", cfg.Server.Type))
	}
	if cfg.PHP.Version != "" {
		if _, ok := phpFPMPortMap[cfg.PHP.Version]; !ok {
			errs = append(errs, fmt.Sprintf("php.version %q is not a supported version", cfg.PHP.Version))
		}
	}

	if len(errs) > 0 {
		return fmt.Errorf("%s", strings.Join(errs, "; "))
	}
	return nil
}

// ToRenderContext converts a SiteConfig into a RenderContext ready for templating.
// logDir is the base directory where log files will be written.
// certBaseDir is the directory containing SSL certificates named after the hostname.
func ToRenderContext(cfg *SiteConfig, logDir, certBaseDir string) *RenderContext {
	port := 80
	if cfg.SSL.Enabled {
		port = 443
	}

	fpmPort := 9082 // default: PHP 8.2
	if p, ok := phpFPMPortMap[cfg.PHP.Version]; ok {
		fpmPort = p
	}

	certPath := cfg.SSL.CertPath
	keyPath := cfg.SSL.KeyPath
	if cfg.SSL.Enabled {
		if certPath == "" {
			certPath = filepath.Join(certBaseDir, cfg.Site.Hostname+".crt")
		}
		if keyPath == "" {
			keyPath = filepath.Join(certBaseDir, cfg.Site.Hostname+".key")
		}
	}

	return &RenderContext{
		Hostname:     cfg.Site.Hostname,
		Aliases:      cfg.Site.Aliases,
		DocumentRoot: cfg.Site.DocumentRoot,
		Port:         port,
		SSL:          cfg.SSL.Enabled,
		SSLCertPath:  certPath,
		SSLKeyPath:   keyPath,
		PHPFPMPort:   fpmPort,
		LogDir:       logDir,
		ServerType:   strings.ToLower(cfg.Server.Type),
	}
}
