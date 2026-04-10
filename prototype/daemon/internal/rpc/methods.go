package rpc

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/nks/wdc/internal/config"
	"github.com/nks/wdc/internal/fileops"
	"github.com/nks/wdc/internal/service"
	tmplpkg "github.com/nks/wdc/internal/template"
	"github.com/nks/wdc/internal/validator"
)

// DaemonDeps groups the dependencies injected into RPC method handlers.
type DaemonDeps struct {
	Manager    *service.Manager
	Renderer   *tmplpkg.Renderer
	VhostDir   string // directory where vhost conf files are written
	LogDir     string
	CertDir    string
	StartedAt  time.Time
}

// RegisterMethods registers all built-in NKS WebDev Console RPC methods on srv.
func RegisterMethods(srv *Server, deps *DaemonDeps) {
	srv.Register("site.create", makeSiteCreate(deps))
	srv.Register("service.start", makeServiceStart(deps))
	srv.Register("service.stop", makeServiceStop(deps))
	srv.Register("service.restart", makeServiceRestart(deps))
	srv.Register("daemon.status", makeDaemonStatus(deps))
}

// ---- site.create -------------------------------------------------------

type siteCreateParams struct {
	TOML string `json:"toml"` // raw TOML content
}

type siteCreateResult struct {
	Hostname   string `json:"hostname"`
	ConfigPath string `json:"config_path"`
	ServerType string `json:"server_type"`
}

func makeSiteCreate(deps *DaemonDeps) HandlerFunc {
	return func(ctx context.Context, raw json.RawMessage) (interface{}, error) {
		var p siteCreateParams
		if err := json.Unmarshal(raw, &p); err != nil {
			return nil, &RPCError{Code: ErrCodeInvalidParams, Message: "invalid params: " + err.Error()}
		}
		if p.TOML == "" {
			return nil, &RPCError{Code: ErrCodeInvalidParams, Message: "toml field is required"}
		}

		cfg, err := config.ParseBytes([]byte(p.TOML))
		if err != nil {
			return nil, &RPCError{Code: ErrCodeInvalidParams, Message: "config parse error: " + err.Error()}
		}

		ctx2 := config.ToRenderContext(cfg, deps.LogDir, deps.CertDir)

		rendered, err := deps.Renderer.Render(ctx2)
		if err != nil {
			return nil, &RPCError{Code: ErrCodeInternal, Message: "render error: " + err.Error()}
		}

		var v *validator.Validator
		if ctx2.ServerType == "nginx" {
			v = validator.NewNginxValidator("")
		} else {
			v = validator.NewApacheValidator("")
		}
		result := v.ValidateBytes([]byte(rendered))
		if !result.Valid {
			return nil, &RPCError{
				Code:    ErrCodeInvalidParams,
				Message: "generated config failed validation",
				Data:    result.Output,
			}
		}

		confPath := fmt.Sprintf("%s/%s.conf", deps.VhostDir, cfg.Site.Hostname)
		if err := fileops.AtomicWrite(confPath, []byte(rendered), fileops.DefaultWriteOptions()); err != nil {
			return nil, &RPCError{Code: ErrCodeInternal, Message: "write error: " + err.Error()}
		}

		return siteCreateResult{
			Hostname:   cfg.Site.Hostname,
			ConfigPath: confPath,
			ServerType: ctx2.ServerType,
		}, nil
	}
}

// ---- service.start -----------------------------------------------------

type serviceParams struct {
	Name string `json:"name"`
}

func makeServiceStart(deps *DaemonDeps) HandlerFunc {
	return func(ctx context.Context, raw json.RawMessage) (interface{}, error) {
		var p serviceParams
		if err := json.Unmarshal(raw, &p); err != nil {
			return nil, &RPCError{Code: ErrCodeInvalidParams, Message: err.Error()}
		}
		if err := deps.Manager.Start(p.Name); err != nil {
			return nil, &RPCError{Code: ErrCodeInternal, Message: err.Error()}
		}
		return map[string]string{"status": "started", "name": p.Name}, nil
	}
}

// ---- service.stop ------------------------------------------------------

func makeServiceStop(deps *DaemonDeps) HandlerFunc {
	return func(ctx context.Context, raw json.RawMessage) (interface{}, error) {
		var p serviceParams
		if err := json.Unmarshal(raw, &p); err != nil {
			return nil, &RPCError{Code: ErrCodeInvalidParams, Message: err.Error()}
		}
		if err := deps.Manager.Stop(p.Name); err != nil {
			return nil, &RPCError{Code: ErrCodeInternal, Message: err.Error()}
		}
		return map[string]string{"status": "stopped", "name": p.Name}, nil
	}
}

// ---- service.restart ---------------------------------------------------

func makeServiceRestart(deps *DaemonDeps) HandlerFunc {
	return func(ctx context.Context, raw json.RawMessage) (interface{}, error) {
		var p serviceParams
		if err := json.Unmarshal(raw, &p); err != nil {
			return nil, &RPCError{Code: ErrCodeInvalidParams, Message: err.Error()}
		}
		if err := deps.Manager.Restart(p.Name); err != nil {
			return nil, &RPCError{Code: ErrCodeInternal, Message: err.Error()}
		}
		return map[string]string{"status": "restarted", "name": p.Name}, nil
	}
}

// ---- daemon.status -----------------------------------------------------

type daemonStatusResult struct {
	Uptime   string                   `json:"uptime"`
	Services []serviceStatusResult    `json:"services"`
}

type serviceStatusResult struct {
	Name   string `json:"name"`
	State  string `json:"state"`
	PID    int    `json:"pid"`
	Uptime string `json:"uptime"`
}

func makeDaemonStatus(deps *DaemonDeps) HandlerFunc {
	return func(ctx context.Context, raw json.RawMessage) (interface{}, error) {
		statuses := deps.Manager.StatusAll()
		svcResults := make([]serviceStatusResult, 0, len(statuses))
		for _, s := range statuses {
			svcResults = append(svcResults, serviceStatusResult{
				Name:   s.Name,
				State:  s.State.String(),
				PID:    s.PID,
				Uptime: s.Uptime.Truncate(time.Second).String(),
			})
		}
		return daemonStatusResult{
			Uptime:   time.Since(deps.StartedAt).Truncate(time.Second).String(),
			Services: svcResults,
		}, nil
	}
}
