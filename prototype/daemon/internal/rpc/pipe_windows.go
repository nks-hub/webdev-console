//go:build windows

package rpc

import (
	"fmt"
	"net"
	"time"

	"github.com/Microsoft/go-winio"
)

const DefaultPipeName = `\\.\pipe\wdc`

// ListenPipe creates a Windows named pipe listener at the given pipe path.
// If pipePath is empty, DefaultPipeName is used.
func ListenPipe(pipePath string) (net.Listener, error) {
	if pipePath == "" {
		pipePath = DefaultPipeName
	}
	cfg := &winio.PipeConfig{
		SecurityDescriptor: "D:P(A;;GA;;;WD)", // allow everyone — tighten for production
		MessageMode:        false,
		InputBufferSize:    65536,
		OutputBufferSize:   65536,
	}
	ln, err := winio.ListenPipe(pipePath, cfg)
	if err != nil {
		return nil, fmt.Errorf("creating named pipe %q: %w", pipePath, err)
	}
	return ln, nil
}

// DialPipe connects to a Windows named pipe with a 5-second timeout.
func DialPipe(pipePath string) (net.Conn, error) {
	if pipePath == "" {
		pipePath = DefaultPipeName
	}
	timeout := 5 * time.Second
	conn, err := winio.DialPipe(pipePath, &timeout)
	if err != nil {
		return nil, fmt.Errorf("dialing named pipe %q: %w", pipePath, err)
	}
	return conn, nil
}
