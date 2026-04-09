// Package template renders web server configuration files from site configs.
package template

import (
	"bytes"
	"embed"
	"fmt"
	"strings"
	"text/template"

	"github.com/nks/devforge/internal/config"
)

//go:embed templates/*.tmpl
var templateFS embed.FS

// Renderer holds compiled templates for all supported server types.
type Renderer struct {
	templates map[string]*template.Template
}

// NewRenderer creates a Renderer and pre-compiles all embedded templates.
func NewRenderer() (*Renderer, error) {
	r := &Renderer{
		templates: make(map[string]*template.Template),
	}

	entries, err := templateFS.ReadDir("templates")
	if err != nil {
		return nil, fmt.Errorf("reading embedded templates dir: %w", err)
	}

	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".tmpl") {
			continue
		}
		data, err := templateFS.ReadFile("templates/" + entry.Name())
		if err != nil {
			return nil, fmt.Errorf("reading template %q: %w", entry.Name(), err)
		}
		name := strings.TrimSuffix(entry.Name(), ".tmpl")
		tmpl, err := template.New(name).Parse(string(data))
		if err != nil {
			return nil, fmt.Errorf("parsing template %q: %w", entry.Name(), err)
		}
		r.templates[name] = tmpl
	}
	return r, nil
}

// Render produces the vhost config string for the given render context.
// serverType must be "apache" or "nginx".
func (r *Renderer) Render(ctx *config.RenderContext) (string, error) {
	key := ctx.ServerType + "_vhost"
	tmpl, ok := r.templates[key]
	if !ok {
		return "", fmt.Errorf("no template registered for server type %q (key: %q)", ctx.ServerType, key)
	}

	var buf bytes.Buffer
	if err := tmpl.Execute(&buf, ctx); err != nil {
		return "", fmt.Errorf("executing template %q: %w", key, err)
	}
	return buf.String(), nil
}

// RenderApache is a convenience wrapper for Apache vhost rendering.
func (r *Renderer) RenderApache(ctx *config.RenderContext) (string, error) {
	ctx.ServerType = "apache"
	return r.Render(ctx)
}

// RenderNginx is a convenience wrapper for Nginx vhost rendering.
func (r *Renderer) RenderNginx(ctx *config.RenderContext) (string, error) {
	ctx.ServerType = "nginx"
	return r.Render(ctx)
}
