// Package dns — port scanner using Windows netstat output.
//
// PortInfo describes the current occupant of a TCP port.
// PortCheck inspects port usage and suggests alternatives.
package dns

import (
	"bufio"
	"bytes"
	"fmt"
	"os/exec"
	"strconv"
	"strings"
)

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

// PortStatus describes the occupancy of a single TCP port.
type PortStatus struct {
	Port        int
	InUse       bool
	PID         int
	ProcessName string
}

// PortCheckResult holds the result for the requested port and a suggested
// alternative when the primary port is occupied.
type PortCheckResult struct {
	Primary     PortStatus
	Alternative *PortStatus // nil when primary is free
}

// wellKnownAlternatives maps commonly used dev ports to their fallbacks.
var wellKnownAlternatives = map[int]int{
	80:   8080,
	443:  8443,
	3306: 3307,
	5432: 5433,
	6379: 6380,
	27017: 27018,
}

// ---------------------------------------------------------------------------
// netstat parsing
// ---------------------------------------------------------------------------

// parseNetstat runs `netstat -ano` and returns a map of port → PID for all
// LISTENING TCP entries.
func parseNetstat() (map[int]int, error) {
	out, err := exec.Command("netstat", "-ano").Output()
	if err != nil {
		return nil, fmt.Errorf("run netstat: %w", err)
	}

	result := make(map[int]int)
	sc := bufio.NewScanner(bytes.NewReader(out))
	for sc.Scan() {
		line := sc.Text()
		if !strings.Contains(strings.ToUpper(line), "LISTENING") {
			continue
		}

		// Typical line (IPv4):
		//   TCP    0.0.0.0:80             0.0.0.0:0              LISTENING       1234
		// Typical line (IPv6):
		//   TCP    [::]:443               [::]:0                 LISTENING       5678
		fields := strings.Fields(line)
		if len(fields) < 5 {
			continue
		}

		localAddr := fields[1]
		pidStr    := fields[4]

		// Extract port from local address
		port := extractPort(localAddr)
		if port <= 0 {
			continue
		}

		pid, err := strconv.Atoi(pidStr)
		if err != nil {
			continue
		}

		// Keep lowest PID if there are duplicates (prefer system listener)
		if existing, ok := result[port]; !ok || pid < existing {
			result[port] = pid
		}
	}
	return result, sc.Err()
}

// extractPort parses the port number from an address like "0.0.0.0:80" or "[::]:443".
func extractPort(addr string) int {
	// Strip IPv6 bracket notation
	if i := strings.LastIndex(addr, ":"); i >= 0 {
		portStr := addr[i+1:]
		port, err := strconv.Atoi(portStr)
		if err != nil {
			return -1
		}
		return port
	}
	return -1
}

// ---------------------------------------------------------------------------
// Process name resolution
// ---------------------------------------------------------------------------

// processName returns the executable name for pid via `tasklist`.
// Returns "unknown" on failure.
func processName(pid int) string {
	out, err := exec.Command("tasklist", "/FI", fmt.Sprintf("PID eq %d", pid), "/NH", "/FO", "CSV").Output()
	if err != nil {
		return "unknown"
	}

	// Output: "process.exe","1234","Console","1","4,096 K"
	line := strings.TrimSpace(string(out))
	if line == "" || strings.Contains(line, "No tasks") {
		return "unknown"
	}

	// First CSV field is the image name, strip the quotes
	parts := strings.SplitN(line, ",", 2)
	if len(parts) == 0 {
		return "unknown"
	}
	return strings.Trim(parts[0], `"`)
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

// CheckPort inspects port availability and returns a PortCheckResult.
// If the primary port is in use and a well-known alternative exists,
// that alternative is also checked and included in the result.
func CheckPort(port int) (*PortCheckResult, error) {
	if port <= 0 || port > 65535 {
		return nil, fmt.Errorf("invalid port number: %d", port)
	}

	listening, err := parseNetstat()
	if err != nil {
		return nil, err
	}

	primary := buildPortStatus(port, listening)
	result  := &PortCheckResult{Primary: primary}

	if !primary.InUse {
		return result, nil
	}

	// Suggest an alternative
	if altPort, ok := wellKnownAlternatives[port]; ok {
		alt := buildPortStatus(altPort, listening)
		result.Alternative = &alt
	}

	return result, nil
}

// buildPortStatus creates a PortStatus by looking up the port in the
// listening map and resolving the process name when occupied.
func buildPortStatus(port int, listening map[int]int) PortStatus {
	pid, inUse := listening[port]
	if !inUse {
		return PortStatus{Port: port, InUse: false}
	}
	return PortStatus{
		Port:        port,
		InUse:       true,
		PID:         pid,
		ProcessName: processName(pid),
	}
}

// CheckPorts inspects multiple ports at once. Shares a single netstat run.
func CheckPorts(ports []int) ([]PortStatus, error) {
	listening, err := parseNetstat()
	if err != nil {
		return nil, err
	}

	results := make([]PortStatus, len(ports))
	for i, p := range ports {
		results[i] = buildPortStatus(p, listening)
	}
	return results, nil
}

// FormatPortStatus returns a human-readable string for a PortStatus.
func FormatPortStatus(s PortStatus) string {
	if !s.InUse {
		return fmt.Sprintf("Port %d: available", s.Port)
	}
	return fmt.Sprintf("Port %d: IN USE by PID %d (%s)", s.Port, s.PID, s.ProcessName)
}
