// Package validator checks generated server config files for syntax errors.
package validator

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"strings"
)

// Result holds the outcome of a config validation run.
type Result struct {
	Valid  bool
	Output string
	Err    error
}

// Validator validates a web server config file.
type Validator struct {
	// ServerBinary is the path to httpd or nginx. If empty, syntax-only
	// validation is performed without shelling out.
	ServerBinary string
	// ServerType distinguishes "apache" from "nginx" validation flags.
	ServerType string
}

// NewApacheValidator returns a Validator for Apache httpd.
// If httpdPath is empty the binary is located via PATH.
func NewApacheValidator(httpdPath string) *Validator {
	if httpdPath == "" {
		httpdPath, _ = exec.LookPath("httpd")
		if httpdPath == "" {
			httpdPath, _ = exec.LookPath("apache2")
		}
	}
	return &Validator{ServerBinary: httpdPath, ServerType: "apache"}
}

// NewNginxValidator returns a Validator for nginx.
func NewNginxValidator(nginxPath string) *Validator {
	if nginxPath == "" {
		nginxPath, _ = exec.LookPath("nginx")
	}
	return &Validator{ServerBinary: nginxPath, ServerType: "nginx"}
}

// ValidateFile validates the config at the given file path.
// If the server binary is not available it falls back to structural
// heuristic validation so tests can pass without a real httpd install.
func (v *Validator) ValidateFile(path string) Result {
	data, err := os.ReadFile(path)
	if err != nil {
		return Result{Err: fmt.Errorf("reading file for validation: %w", err)}
	}
	return v.ValidateBytes(data)
}

// ValidateBytes validates raw config content.
func (v *Validator) ValidateBytes(data []byte) Result {
	// Always run heuristic checks first.
	if err := heuristicCheck(string(data), v.ServerType); err != nil {
		return Result{Valid: false, Output: err.Error(), Err: err}
	}

	if v.ServerBinary == "" {
		return Result{
			Valid:  true,
			Output: "heuristic validation passed (no server binary available)",
		}
	}
	return v.execValidation(data)
}

// execValidation writes config to a temp file and invokes the server binary.
func (v *Validator) execValidation(data []byte) Result {
	tmp, err := os.CreateTemp("", "devforge-validate-*.conf")
	if err != nil {
		return Result{Err: fmt.Errorf("creating temp file: %w", err)}
	}
	defer os.Remove(tmp.Name())

	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		return Result{Err: fmt.Errorf("writing temp config: %w", err)}
	}
	tmp.Close()

	var args []string
	switch v.ServerType {
	case "nginx":
		args = []string{"-t", "-c", tmp.Name()}
	default: // apache
		args = []string{"-t", "-f", tmp.Name()}
	}

	cmd := exec.Command(v.ServerBinary, args...)
	var out bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &out

	runErr := cmd.Run()
	output := out.String()

	if runErr != nil {
		return Result{Valid: false, Output: output, Err: fmt.Errorf("config validation failed: %w", runErr)}
	}
	return Result{Valid: true, Output: output}
}

// heuristicCheck performs fast structural checks without invoking a binary.
func heuristicCheck(content, serverType string) error {
	switch serverType {
	case "nginx":
		return nginxHeuristic(content)
	default:
		return apacheHeuristic(content)
	}
}

func apacheHeuristic(content string) error {
	opens := strings.Count(content, "<VirtualHost")
	closes := strings.Count(content, "</VirtualHost>")
	if opens == 0 {
		return fmt.Errorf("apache config has no <VirtualHost> block")
	}
	if opens != closes {
		return fmt.Errorf("apache config has mismatched VirtualHost tags (%d open, %d close)", opens, closes)
	}
	if !strings.Contains(content, "ServerName") {
		return fmt.Errorf("apache config missing ServerName directive")
	}
	if !strings.Contains(content, "DocumentRoot") {
		return fmt.Errorf("apache config missing DocumentRoot directive")
	}
	return nil
}

func nginxHeuristic(content string) error {
	if !strings.Contains(content, "server {") {
		return fmt.Errorf("nginx config missing server block")
	}
	opens := strings.Count(content, "{")
	closes := strings.Count(content, "}")
	if opens != closes {
		return fmt.Errorf("nginx config has mismatched braces (%d open, %d close)", opens, closes)
	}
	if !strings.Contains(content, "server_name") {
		return fmt.Errorf("nginx config missing server_name directive")
	}
	return nil
}
