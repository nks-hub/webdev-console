// Package fileops provides atomic file write operations.
package fileops

import (
	"fmt"
	"os"
	"path/filepath"
)

// WriteOptions controls atomic write behaviour.
type WriteOptions struct {
	// Perm is the file permission for the final file (default 0644).
	Perm os.FileMode
	// BackupExisting renames an existing destination file to <path>.bak
	// before the atomic rename.
	BackupExisting bool
}

// DefaultWriteOptions returns sensible defaults for config file writes.
func DefaultWriteOptions() WriteOptions {
	return WriteOptions{Perm: 0644, BackupExisting: true}
}

// AtomicWrite writes data to a temporary file in the same directory as dst,
// then renames it to dst. This guarantees that readers never see a partially
// written file on POSIX and provides best-effort atomicity on Windows.
//
// On Windows rename is not truly atomic when the destination already exists,
// but the temp-then-rename pattern still protects against truncated writes.
func AtomicWrite(dst string, data []byte, opts WriteOptions) error {
	if opts.Perm == 0 {
		opts.Perm = 0644
	}

	dir := filepath.Dir(dst)
	if err := os.MkdirAll(dir, 0755); err != nil {
		return fmt.Errorf("creating directory %q: %w", dir, err)
	}

	// Write to a temp file in the same directory so the rename stays on one
	// filesystem (avoids cross-device link errors).
	tmp, err := os.CreateTemp(dir, ".devforge-tmp-*")
	if err != nil {
		return fmt.Errorf("creating temp file in %q: %w", dir, err)
	}
	tmpName := tmp.Name()

	// Always clean up the temp file if something goes wrong.
	success := false
	defer func() {
		if !success {
			os.Remove(tmpName)
		}
	}()

	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		return fmt.Errorf("writing to temp file: %w", err)
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return fmt.Errorf("syncing temp file: %w", err)
	}
	if err := tmp.Close(); err != nil {
		return fmt.Errorf("closing temp file: %w", err)
	}
	if err := os.Chmod(tmpName, opts.Perm); err != nil {
		return fmt.Errorf("setting permissions on temp file: %w", err)
	}

	// Optional backup of the previous file.
	if opts.BackupExisting {
		if _, statErr := os.Stat(dst); statErr == nil {
			bakPath := dst + ".bak"
			// Ignore backup errors — the main write still proceeds.
			_ = os.Rename(dst, bakPath)
		}
	}

	if err := os.Rename(tmpName, dst); err != nil {
		return fmt.Errorf("atomic rename %q -> %q: %w", tmpName, dst, err)
	}

	success = true
	return nil
}

// ReadIfExists reads a file and returns its contents. If the file does not
// exist it returns nil, nil so callers can distinguish missing from error.
func ReadIfExists(path string) ([]byte, error) {
	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("reading %q: %w", path, err)
	}
	return data, nil
}

// EnsureDir creates a directory and all parents if they don't already exist.
func EnsureDir(path string) error {
	if err := os.MkdirAll(path, 0755); err != nil {
		return fmt.Errorf("ensuring directory %q: %w", path, err)
	}
	return nil
}
