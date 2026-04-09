// Package service manages the lifecycle of external server processes.
package service

import (
	"fmt"
	"os"
	"os/exec"
	"sync"
	"time"
)

// State enumerates the lifecycle states of a managed process.
type State int

const (
	StateStopped State = iota
	StateStarting
	StateRunning
	StateStopping
	StateError
)

func (s State) String() string {
	switch s {
	case StateStopped:
		return "stopped"
	case StateStarting:
		return "starting"
	case StateRunning:
		return "running"
	case StateStopping:
		return "stopping"
	case StateError:
		return "error"
	default:
		return "unknown"
	}
}

// Process wraps a single OS process with state tracking.
type Process struct {
	mu      sync.Mutex
	name    string
	binary  string
	args    []string
	cmd     *exec.Cmd
	state   State
	pid     int
	startAt time.Time
	lastErr error
}

// NewProcess creates a Process descriptor. binary is the executable path;
// args are the arguments passed on startup.
func NewProcess(name, binary string, args ...string) *Process {
	return &Process{
		name:   name,
		binary: binary,
		args:   args,
		state:  StateStopped,
	}
}

// Start launches the process. It is a no-op if the process is already running.
func (p *Process) Start() error {
	p.mu.Lock()
	defer p.mu.Unlock()

	if p.state == StateRunning || p.state == StateStarting {
		return fmt.Errorf("process %q is already %s", p.name, p.state)
	}

	p.state = StateStarting
	p.cmd = exec.Command(p.binary, p.args...)
	p.cmd.Stdout = os.Stdout
	p.cmd.Stderr = os.Stderr

	if err := p.cmd.Start(); err != nil {
		p.state = StateError
		p.lastErr = err
		return fmt.Errorf("starting process %q: %w", p.name, err)
	}

	p.pid = p.cmd.Process.Pid
	p.state = StateRunning
	p.startAt = time.Now()
	p.lastErr = nil

	// Reap the process in the background so p.state stays accurate.
	go p.wait()
	return nil
}

// Stop sends an interrupt signal and waits up to timeout for the process to exit.
func (p *Process) Stop(timeout time.Duration) error {
	p.mu.Lock()
	if p.state != StateRunning {
		p.mu.Unlock()
		return fmt.Errorf("process %q is not running (state: %s)", p.name, p.state)
	}
	p.state = StateStopping
	proc := p.cmd.Process
	p.mu.Unlock()

	if err := proc.Signal(os.Interrupt); err != nil {
		// Interrupt may not be supported on Windows; fall back to Kill.
		_ = proc.Kill()
	}

	done := make(chan struct{})
	go func() {
		_ = p.cmd.Wait()
		close(done)
	}()

	select {
	case <-done:
	case <-time.After(timeout):
		_ = proc.Kill()
		<-done
	}

	p.mu.Lock()
	p.state = StateStopped
	p.pid = 0
	p.mu.Unlock()
	return nil
}

// IsRunning reports whether the process is currently in the running state.
func (p *Process) IsRunning() bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.state == StateRunning
}

// PID returns the OS process ID, or 0 if not running.
func (p *Process) PID() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.pid
}

// Status returns a snapshot of the process state.
func (p *Process) Status() ProcessStatus {
	p.mu.Lock()
	defer p.mu.Unlock()
	uptime := time.Duration(0)
	if p.state == StateRunning {
		uptime = time.Since(p.startAt)
	}
	return ProcessStatus{
		Name:    p.name,
		State:   p.state,
		PID:     p.pid,
		Uptime:  uptime,
		LastErr: p.lastErr,
	}
}

// ProcessStatus is a point-in-time snapshot returned by Status.
type ProcessStatus struct {
	Name    string
	State   State
	PID     int
	Uptime  time.Duration
	LastErr error
}

// wait is run in a goroutine to reap the child and update state.
func (p *Process) wait() {
	err := p.cmd.Wait()
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.state != StateStopping {
		p.state = StateError
		p.lastErr = err
	} else {
		p.state = StateStopped
	}
	p.pid = 0
}
