// DevForge daemon — manages web server configs and service lifecycle via JSON-RPC.
package main

import (
	"context"
	"flag"
	"log/slog"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/nks/devforge/internal/rpc"
	"github.com/nks/devforge/internal/service"
	tmplpkg "github.com/nks/devforge/internal/template"
)

func main() {
	pipePath := flag.String("pipe", "", "named pipe / unix socket path (default: platform default)")
	vhostDir := flag.String("vhost-dir", os.TempDir(), "directory to write vhost config files")
	logDir := flag.String("log-dir", os.TempDir(), "directory for web server log files")
	certDir := flag.String("cert-dir", os.TempDir(), "directory containing SSL certificates")
	verbose := flag.Bool("v", false, "verbose logging")
	flag.Parse()

	level := slog.LevelInfo
	if *verbose {
		level = slog.LevelDebug
	}
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: level}))
	slog.SetDefault(logger)

	renderer, err := tmplpkg.NewRenderer()
	if err != nil {
		logger.Error("failed to initialise template renderer", "err", err)
		os.Exit(1)
	}

	manager := service.NewManager()

	deps := &rpc.DaemonDeps{
		Manager:   manager,
		Renderer:  renderer,
		VhostDir:  *vhostDir,
		LogDir:    *logDir,
		CertDir:   *certDir,
		StartedAt: time.Now(),
	}

	srv := rpc.NewServer(logger)
	rpc.RegisterMethods(srv, deps)

	ln, err := rpc.ListenPipe(*pipePath)
	if err != nil {
		logger.Error("failed to create listener", "err", err)
		os.Exit(1)
	}
	logger.Info("devforge daemon starting", "pipe", ln.Addr())

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if err := srv.Listen(ctx, ln); err != nil {
		logger.Error("rpc server error", "err", err)
		os.Exit(1)
	}
	logger.Info("devforge daemon shut down cleanly")
}
