//go:build !windows

package rpc

import (
	"fmt"
	"net"
	"os"
)

const DefaultPipeName = "/tmp/devforge.sock"

// ListenPipe creates a Unix domain socket listener at the given path.
// If socketPath is empty, DefaultPipeName is used.
func ListenPipe(socketPath string) (net.Listener, error) {
	if socketPath == "" {
		socketPath = DefaultPipeName
	}
	// Remove stale socket file from a previous run.
	_ = os.Remove(socketPath)
	ln, err := net.Listen("unix", socketPath)
	if err != nil {
		return nil, fmt.Errorf("creating unix socket %q: %w", socketPath, err)
	}
	return ln, nil
}

// DialPipe connects to a Unix domain socket.
func DialPipe(socketPath string) (net.Conn, error) {
	if socketPath == "" {
		socketPath = DefaultPipeName
	}
	conn, err := net.Dial("unix", socketPath)
	if err != nil {
		return nil, fmt.Errorf("dialing unix socket %q: %w", socketPath, err)
	}
	return conn, nil
}
