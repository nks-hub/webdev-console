package service

import (
	"runtime"
	"testing"
	"time"
)

func nopProcess(name string) *Process {
	// A process that exits quickly — "cmd /c echo" on Windows, "true" on Unix.
	if runtime.GOOS == "windows" {
		return NewProcess(name, "cmd", "/c", "echo", "devforge-test")
	}
	return NewProcess(name, "true")
}

func longProcess(name string) *Process {
	// A process that stays alive until killed.
	// We use PowerShell Start-Sleep which works in both native Windows and
	// MSYS2/bash environments since powershell.exe is always available on
	// Windows regardless of which shell the test runner uses.
	if runtime.GOOS == "windows" {
		return NewProcess(name, "powershell", "-NoProfile", "-Command", "Start-Sleep", "-Seconds", "60")
	}
	return NewProcess(name, "sleep", "60")
}

func TestManager_RegisterAndStart(t *testing.T) {
	m := NewManager()
	p := nopProcess("echo")
	if err := m.Register(p); err != nil {
		t.Fatalf("Register: %v", err)
	}
	if err := m.Start("echo"); err != nil {
		t.Fatalf("Start: %v", err)
	}
	// Allow process to finish naturally.
	time.Sleep(200 * time.Millisecond)
}

func TestManager_RegisterDuplicate(t *testing.T) {
	m := NewManager()
	p := nopProcess("dup")
	m.Register(p)
	if err := m.Register(nopProcess("dup")); err == nil {
		t.Error("expected error on duplicate register")
	}
}

func TestManager_StartUnknown(t *testing.T) {
	m := NewManager()
	if err := m.Start("ghost"); err == nil {
		t.Error("expected error for unknown process")
	}
}

func TestManager_StopRunningProcess(t *testing.T) {
	m := NewManager()
	p := longProcess("sleepy")
	m.Register(p)
	if err := m.Start("sleepy"); err != nil {
		t.Fatalf("Start: %v", err)
	}

	// Give process time to start.
	time.Sleep(100 * time.Millisecond)

	alive, _ := m.HealthCheck("sleepy")
	if !alive {
		t.Error("expected process to be running")
	}

	if err := m.StopTimeout("sleepy", 3*time.Second); err != nil {
		t.Fatalf("Stop: %v", err)
	}

	alive, _ = m.HealthCheck("sleepy")
	if alive {
		t.Error("expected process to be stopped")
	}
}

func TestManager_StatusAll(t *testing.T) {
	m := NewManager()
	m.Register(nopProcess("a"))
	m.Register(nopProcess("b"))

	statuses := m.StatusAll()
	if len(statuses) != 2 {
		t.Errorf("StatusAll returned %d entries, want 2", len(statuses))
	}
}

func TestProcess_StateString(t *testing.T) {
	cases := []struct {
		s    State
		want string
	}{
		{StateStopped, "stopped"},
		{StateRunning, "running"},
		{StateStarting, "starting"},
		{StateStopping, "stopping"},
		{StateError, "error"},
	}
	for _, c := range cases {
		if got := c.s.String(); got != c.want {
			t.Errorf("State(%d).String() = %q, want %q", c.s, got, c.want)
		}
	}
}
