package fileops

import (
	"os"
	"path/filepath"
	"testing"
)

func TestAtomicWrite_CreatesFile(t *testing.T) {
	dir := t.TempDir()
	dst := filepath.Join(dir, "test.conf")
	content := []byte("hello config")

	if err := AtomicWrite(dst, content, DefaultWriteOptions()); err != nil {
		t.Fatalf("AtomicWrite: %v", err)
	}

	got, err := os.ReadFile(dst)
	if err != nil {
		t.Fatalf("reading result: %v", err)
	}
	if string(got) != string(content) {
		t.Errorf("content = %q, want %q", got, content)
	}
}

func TestAtomicWrite_OverwritesExistingFile(t *testing.T) {
	dir := t.TempDir()
	dst := filepath.Join(dir, "test.conf")

	original := []byte("original content")
	if err := os.WriteFile(dst, original, 0644); err != nil {
		t.Fatal(err)
	}

	updated := []byte("updated content")
	opts := WriteOptions{Perm: 0644, BackupExisting: false}
	if err := AtomicWrite(dst, updated, opts); err != nil {
		t.Fatalf("AtomicWrite: %v", err)
	}

	got, _ := os.ReadFile(dst)
	if string(got) != string(updated) {
		t.Errorf("content = %q, want %q", got, updated)
	}
}

func TestAtomicWrite_CreatesBackup(t *testing.T) {
	dir := t.TempDir()
	dst := filepath.Join(dir, "test.conf")

	if err := os.WriteFile(dst, []byte("v1"), 0644); err != nil {
		t.Fatal(err)
	}

	opts := WriteOptions{Perm: 0644, BackupExisting: true}
	if err := AtomicWrite(dst, []byte("v2"), opts); err != nil {
		t.Fatalf("AtomicWrite: %v", err)
	}

	bak := dst + ".bak"
	if _, err := os.Stat(bak); os.IsNotExist(err) {
		t.Error("backup file was not created")
	}
	bakContent, _ := os.ReadFile(bak)
	if string(bakContent) != "v1" {
		t.Errorf("backup content = %q, want %q", bakContent, "v1")
	}
}

func TestAtomicWrite_CreatesParentDirs(t *testing.T) {
	dir := t.TempDir()
	dst := filepath.Join(dir, "deep", "nested", "dir", "test.conf")

	if err := AtomicWrite(dst, []byte("data"), DefaultWriteOptions()); err != nil {
		t.Fatalf("AtomicWrite with nested path: %v", err)
	}

	if _, err := os.Stat(dst); err != nil {
		t.Errorf("file not found after write: %v", err)
	}
}

func TestAtomicWrite_NoTempFileLeftOnSuccess(t *testing.T) {
	dir := t.TempDir()
	dst := filepath.Join(dir, "test.conf")

	if err := AtomicWrite(dst, []byte("clean"), DefaultWriteOptions()); err != nil {
		t.Fatal(err)
	}

	entries, _ := os.ReadDir(dir)
	for _, e := range entries {
		name := e.Name()
		if name != "test.conf" && name != "test.conf.bak" {
			t.Errorf("unexpected leftover file: %q", name)
		}
	}
}

func TestReadIfExists_ExistingFile(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "existing.txt")
	os.WriteFile(p, []byte("data"), 0644)

	got, err := ReadIfExists(p)
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != "data" {
		t.Errorf("got %q, want %q", got, "data")
	}
}

func TestReadIfExists_MissingFile(t *testing.T) {
	got, err := ReadIfExists("/nonexistent/path/file.txt")
	if err != nil {
		t.Fatalf("expected nil error for missing file, got %v", err)
	}
	if got != nil {
		t.Error("expected nil data for missing file")
	}
}

func TestEnsureDir(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(dir, "a", "b", "c")

	if err := EnsureDir(target); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(target); err != nil {
		t.Errorf("directory not created: %v", err)
	}

	// Calling again on existing dir must be idempotent.
	if err := EnsureDir(target); err != nil {
		t.Errorf("second EnsureDir: %v", err)
	}
}
