// Package dns provides programmatic management of the Windows hosts file
// for the DevForge daemon. It operates only within a clearly delimited
// managed block so user content is never touched.
package dns

import (
	"bufio"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const (
	hostsFile  = `C:\Windows\System32\drivers\etc\hosts`
	backupDir  = `C:\DevForge\backups`
	blockStart = "# >>> DevForge Managed - DO NOT EDIT <<<"
	blockEnd   = "# <<< DevForge Managed >>>"
)

var domainRe = regexp.MustCompile(`^(?:[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}$`)

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

// HostEntry represents a single line inside the managed block.
type HostEntry struct {
	IP      string
	Domains []string // first is primary, rest are aliases
}

// String returns the hosts-file representation of the entry.
func (e HostEntry) String() string {
	return e.IP + "\t" + strings.Join(e.Domains, "\t")
}

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

// ValidateDomain returns an error if name is not a valid hostname.
func ValidateDomain(name string) error {
	if !domainRe.MatchString(name) {
		return fmt.Errorf("invalid domain format: %q", name)
	}
	return nil
}

// ---------------------------------------------------------------------------
// Low-level file I/O
// ---------------------------------------------------------------------------

// readLines reads the hosts file and returns its lines.
func readLines() ([]string, error) {
	f, err := os.Open(hostsFile)
	if err != nil {
		return nil, fmt.Errorf("open hosts file: %w", err)
	}
	defer f.Close()

	var lines []string
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		lines = append(lines, sc.Text())
	}
	return lines, sc.Err()
}

// writeLines atomically replaces the hosts file using a temp-file + rename.
// On Windows, os.Rename across volumes is not atomic, but within the same
// volume (C:\Windows is always on C:\) it is effectively atomic.
func writeLines(lines []string) error {
	// Write to a temp file in the same directory to stay on the same volume.
	dir := filepath.Dir(hostsFile)
	tmp, err := os.CreateTemp(dir, ".hosts_tmp_")
	if err != nil {
		return fmt.Errorf("create temp file: %w", err)
	}
	tmpName := tmp.Name()

	defer func() {
		_ = os.Remove(tmpName) // clean up on failure; harmless if already renamed
	}()

	w := bufio.NewWriter(tmp)
	for _, line := range lines {
		if _, err := fmt.Fprintln(w, line); err != nil {
			_ = tmp.Close()
			return fmt.Errorf("write temp file: %w", err)
		}
	}
	if err := w.Flush(); err != nil {
		_ = tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}

	// Replace hosts file
	if err := os.Rename(tmpName, hostsFile); err != nil {
		return fmt.Errorf("rename temp to hosts: %w", err)
	}
	return nil
}

// ---------------------------------------------------------------------------
// Managed block helpers
// ---------------------------------------------------------------------------

// blockBounds returns the line indices of the start and end markers,
// or (-1, -1) if the block does not exist.
func blockBounds(lines []string) (start, end int) {
	start, end = -1, -1
	for i, l := range lines {
		switch strings.TrimSpace(l) {
		case blockStart:
			start = i
		case blockEnd:
			end = i
		}
	}
	return
}

// parseManagedBlock returns the HostEntry values inside the managed block.
func parseManagedBlock(lines []string) []HostEntry {
	start, end := blockBounds(lines)
	if start < 0 || end < 0 || end <= start {
		return nil
	}

	var entries []HostEntry
	for i := start + 1; i < end; i++ {
		line := strings.TrimSpace(lines[i])
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}
		entries = append(entries, HostEntry{
			IP:      fields[0],
			Domains: fields[1:],
		})
	}
	return entries
}

// setManagedBlock replaces (or inserts) the managed block in lines with
// the provided entries. Returns the updated lines slice.
func setManagedBlock(lines []string, entries []HostEntry) []string {
	blockLines := []string{blockStart}
	for _, e := range entries {
		blockLines = append(blockLines, e.String())
	}
	blockLines = append(blockLines, blockEnd)

	start, end := blockBounds(lines)
	if start < 0 {
		// No block — append
		out := make([]string, len(lines))
		copy(out, lines)
		out = append(out, "")
		out = append(out, blockLines...)
		return out
	}

	// Replace existing block
	out := make([]string, 0, len(lines))
	for i := 0; i < len(lines); i++ {
		if i == start {
			out = append(out, blockLines...)
			i = end
		} else {
			out = append(out, lines[i])
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// Backup
// ---------------------------------------------------------------------------

// Backup creates a timestamped copy of the hosts file in backupDir.
// Returns the path to the backup file.
func Backup() (string, error) {
	if err := os.MkdirAll(backupDir, 0o755); err != nil {
		return "", fmt.Errorf("create backup dir: %w", err)
	}

	timestamp := time.Now().Format("20060102-150405")
	dst := filepath.Join(backupDir, "hosts."+timestamp+".bak")

	src, err := os.ReadFile(hostsFile)
	if err != nil {
		return "", fmt.Errorf("read hosts file: %w", err)
	}
	if err := os.WriteFile(dst, src, 0o644); err != nil {
		return "", fmt.Errorf("write backup: %w", err)
	}
	return dst, nil
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

// AddEntry adds a domain (and optional aliases) to the managed block.
// IP defaults to "127.0.0.1" when empty.
// The operation is idempotent: if all names already exist, no file is written.
func AddEntry(domain, ip string, aliases ...string) error {
	if err := ValidateDomain(domain); err != nil {
		return err
	}
	if ip == "" {
		ip = "127.0.0.1"
	}
	for _, a := range aliases {
		if err := ValidateDomain(a); err != nil {
			return err
		}
	}

	names := append([]string{domain}, aliases...)

	lines, err := readLines()
	if err != nil {
		return err
	}

	existing := parseManagedBlock(lines)

	// Idempotency check
	allPresent := true
	for _, name := range names {
		found := false
		for _, e := range existing {
			for _, d := range e.Domains {
				if d == name {
					found = true
					break
				}
			}
			if found {
				break
			}
		}
		if !found {
			allPresent = false
			break
		}
	}
	if allPresent {
		return nil // no-op
	}

	if _, err := Backup(); err != nil {
		return fmt.Errorf("backup before add: %w", err)
	}

	// Remove any existing entries that overlap with the names being added
	var kept []HostEntry
	for _, e := range existing {
		overlap := false
		for _, d := range e.Domains {
			for _, name := range names {
				if d == name {
					overlap = true
					break
				}
			}
			if overlap {
				break
			}
		}
		if !overlap {
			kept = append(kept, e)
		}
	}
	kept = append(kept, HostEntry{IP: ip, Domains: names})

	updated := setManagedBlock(lines, kept)
	return writeLines(updated)
}

// RemoveEntry removes all managed block entries that contain the given domain.
func RemoveEntry(domain string) error {
	lines, err := readLines()
	if err != nil {
		return err
	}

	existing := parseManagedBlock(lines)

	var kept []HostEntry
	removed := false
	for _, e := range existing {
		hasDomain := false
		for _, d := range e.Domains {
			if d == domain {
				hasDomain = true
				break
			}
		}
		if hasDomain {
			removed = true
		} else {
			kept = append(kept, e)
		}
	}

	if !removed {
		return fmt.Errorf("domain %q not found in managed block", domain)
	}

	if _, err := Backup(); err != nil {
		return fmt.Errorf("backup before remove: %w", err)
	}

	updated := setManagedBlock(lines, kept)
	return writeLines(updated)
}

// ListEntries returns all HostEntry values in the managed block.
func ListEntries() ([]HostEntry, error) {
	lines, err := readLines()
	if err != nil {
		return nil, err
	}
	return parseManagedBlock(lines), nil
}

// CleanEntries removes all entries from the managed block.
func CleanEntries() error {
	lines, err := readLines()
	if err != nil {
		return err
	}
	if _, err := Backup(); err != nil {
		return fmt.Errorf("backup before clean: %w", err)
	}
	updated := setManagedBlock(lines, nil)
	return writeLines(updated)
}

// RestoreBackup restores the hosts file from a backup created by Backup().
// Pass an empty string to use the most recently created backup.
func RestoreBackup(backupPath string) error {
	if backupPath == "" {
		entries, err := os.ReadDir(backupDir)
		if err != nil {
			return fmt.Errorf("read backup dir: %w", err)
		}
		var latest string
		var latestTime time.Time
		for _, e := range entries {
			if !e.IsDir() && strings.HasPrefix(e.Name(), "hosts.") {
				info, _ := e.Info()
				if info.ModTime().After(latestTime) {
					latestTime = info.ModTime()
					latest = filepath.Join(backupDir, e.Name())
				}
			}
		}
		if latest == "" {
			return errors.New("no backups found in " + backupDir)
		}
		backupPath = latest
	}

	// Back up current state before overwriting
	if _, err := Backup(); err != nil {
		return fmt.Errorf("backup current state: %w", err)
	}

	data, err := os.ReadFile(backupPath)
	if err != nil {
		return fmt.Errorf("read backup file: %w", err)
	}
	return os.WriteFile(hostsFile, data, 0o644)
}

// ---------------------------------------------------------------------------
// Admin privilege check (Windows-specific)
// ---------------------------------------------------------------------------

// IsAdmin returns true when the current process has administrator privileges.
// It uses OpenProcessToken + GetTokenInformation(TokenElevation) which is
// available in the standard library on Windows without extra dependencies.
func IsAdmin() bool {
	token, err := syscall.OpenCurrentProcessToken()
	if err != nil {
		return false
	}
	defer token.Close()

	// TokenElevation = 20
	const tokenElevation = 20
	var elevation uint32
	var returnedLen uint32
	err = syscall.GetTokenInformation(
		token,
		tokenElevation,
		(*byte)(unsafe.Pointer(&elevation)),
		uint32(unsafe.Sizeof(elevation)),
		&returnedLen,
	)
	if err != nil {
		return false
	}
	return elevation != 0
}
