package service

import (
	"fmt"
	"sync"
	"time"
)

// Manager owns a registry of named processes and exposes a safe API for
// starting, stopping, and querying them.
type Manager struct {
	mu        sync.RWMutex
	processes map[string]*Process
}

// NewManager creates an empty Manager.
func NewManager() *Manager {
	return &Manager{
		processes: make(map[string]*Process),
	}
}

// Register adds a process definition. Returns an error if the name is already taken.
func (m *Manager) Register(p *Process) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if _, exists := m.processes[p.name]; exists {
		return fmt.Errorf("process %q is already registered", p.name)
	}
	m.processes[p.name] = p
	return nil
}

// Unregister removes a process from the registry. The process must be stopped first.
func (m *Manager) Unregister(name string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	p, ok := m.processes[name]
	if !ok {
		return fmt.Errorf("process %q not found", name)
	}
	if p.IsRunning() {
		return fmt.Errorf("cannot unregister running process %q", name)
	}
	delete(m.processes, name)
	return nil
}

// Start starts the named process.
func (m *Manager) Start(name string) error {
	p, err := m.get(name)
	if err != nil {
		return err
	}
	return p.Start()
}

// Stop stops the named process with a 10-second timeout.
func (m *Manager) Stop(name string) error {
	return m.StopTimeout(name, 10*time.Second)
}

// StopTimeout stops the named process with an explicit timeout.
func (m *Manager) StopTimeout(name string, timeout time.Duration) error {
	p, err := m.get(name)
	if err != nil {
		return err
	}
	return p.Stop(timeout)
}

// Restart stops then starts the named process.
func (m *Manager) Restart(name string) error {
	p, err := m.get(name)
	if err != nil {
		return err
	}
	if p.IsRunning() {
		if err := p.Stop(10 * time.Second); err != nil {
			return fmt.Errorf("stop during restart of %q: %w", name, err)
		}
	}
	return p.Start()
}

// Status returns the current status of the named process.
func (m *Manager) Status(name string) (ProcessStatus, error) {
	p, err := m.get(name)
	if err != nil {
		return ProcessStatus{}, err
	}
	return p.Status(), nil
}

// StatusAll returns a snapshot of every registered process.
func (m *Manager) StatusAll() []ProcessStatus {
	m.mu.RLock()
	defer m.mu.RUnlock()
	statuses := make([]ProcessStatus, 0, len(m.processes))
	for _, p := range m.processes {
		statuses = append(statuses, p.Status())
	}
	return statuses
}

// HealthCheck returns true if the named process is in the running state.
func (m *Manager) HealthCheck(name string) (bool, error) {
	p, err := m.get(name)
	if err != nil {
		return false, err
	}
	return p.IsRunning(), nil
}

func (m *Manager) get(name string) (*Process, error) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	p, ok := m.processes[name]
	if !ok {
		return nil, fmt.Errorf("process %q not found", name)
	}
	return p, nil
}
