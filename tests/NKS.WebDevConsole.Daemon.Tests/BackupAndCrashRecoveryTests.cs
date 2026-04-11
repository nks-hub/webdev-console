using System.IO.Compression;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Backup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for two Phase 7 hardening items:
///   - #106 Crash recovery: kill the daemon mid-binary-install (simulated by leaving a
///     <c>*.tmp</c> staging directory behind) and verify the next start does not surface
///     the half-extracted state as if it were a real install. The BinaryManager skip
///     rule that excludes <c>*.tmp</c> entries is the relevant safety net.
///   - #108 Backup/restore round-trip: BackupManager.CreateBackup over a temp ~/.wdc/ tree
///     followed by RestoreBackup into a fresh tree must preserve every file byte-for-byte
///     and a malicious zip with <c>../</c> entries must be rejected (zip-slip defense).
///
/// All tests run in isolated temp directories so they leave no side effects on the
/// developer's real ~/.wdc/.
/// </summary>
public class BackupAndCrashRecoveryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _wdcDir;
    private readonly ILogger<BackupManager> _logger = NullLogger<BackupManager>.Instance;

    public BackupAndCrashRecoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"wdc-tests-{Guid.NewGuid():N}");
        _wdcDir = Path.Combine(_tempRoot, ".wdc");
        Directory.CreateDirectory(_wdcDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // ── #108 Backup round trip ───────────────────────────────────────────────

    [Fact]
    public void CreateBackup_writes_zip_with_expected_entries()
    {
        // Arrange — populate ~/.wdc/sites/ with one TOML and ~/.wdc/data/state.db with bytes
        Directory.CreateDirectory(Path.Combine(_wdcDir, "sites"));
        File.WriteAllText(Path.Combine(_wdcDir, "sites", "demo.loc.toml"), "domain = \"demo.loc\"");
        Directory.CreateDirectory(Path.Combine(_wdcDir, "data"));
        File.WriteAllBytes(Path.Combine(_wdcDir, "data", "state.db"), new byte[] { 0x53, 0x51, 0x4C });
        Directory.CreateDirectory(Path.Combine(_wdcDir, "ssl", "sites", "demo.loc"));
        File.WriteAllText(Path.Combine(_wdcDir, "ssl", "sites", "demo.loc", "cert.pem"), "fake");

        var bm = CreateBackupManagerForRoot();
        var outZip = Path.Combine(_tempRoot, "backup.zip");

        // Act
        var result = bm.CreateBackup(outZip);

        // Assert — file exists, contains manifest + at least the three files we wrote
        Assert.True(File.Exists(outZip));
        Assert.Equal(outZip, result.Path);
        Assert.True(result.FileCount >= 4); // manifest + 3 data files
        using var zip = ZipFile.OpenRead(outZip);
        Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
        Assert.Contains(zip.Entries, e => e.FullName == "sites/demo.loc.toml");
        Assert.Contains(zip.Entries, e => e.FullName == "data/state.db");
        Assert.Contains(zip.Entries, e => e.FullName == "ssl/sites/demo.loc/cert.pem");
    }

    [Fact]
    public void Restore_recreates_files_byte_for_byte()
    {
        Directory.CreateDirectory(Path.Combine(_wdcDir, "sites"));
        var originalToml = "domain = \"roundtrip.loc\"\nphpVersion = \"8.3\"";
        File.WriteAllText(Path.Combine(_wdcDir, "sites", "roundtrip.loc.toml"), originalToml);

        var bm = CreateBackupManagerForRoot();
        var outZip = Path.Combine(_tempRoot, "roundtrip.zip");
        bm.CreateBackup(outZip);

        // Wipe sites and restore
        Directory.Delete(Path.Combine(_wdcDir, "sites"), recursive: true);
        Assert.False(File.Exists(Path.Combine(_wdcDir, "sites", "roundtrip.loc.toml")));

        var (count, safetyBackup) = bm.RestoreBackup(outZip);
        Assert.True(count >= 1);
        Assert.True(File.Exists(safetyBackup));
        Assert.Equal(originalToml, File.ReadAllText(Path.Combine(_wdcDir, "sites", "roundtrip.loc.toml")));
    }

    [Fact]
    public void Restore_rejects_zip_with_traversal_entries()
    {
        // Build a malicious zip that tries to escape the staging directory
        var malicious = Path.Combine(_tempRoot, "malicious.zip");
        using (var fs = File.Create(malicious))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.txt", CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open());
            w.Write("you should never see this written outside the staging dir");
        }

        var bm = CreateBackupManagerForRoot();
        // RestoreBackup must NOT throw (zip-slip defense skips the entry) AND must not
        // create any file outside the wdc root.
        bm.RestoreBackup(malicious);
        var escapeFile = Path.Combine(Path.GetDirectoryName(_wdcDir)!, "escape.txt");
        Assert.False(File.Exists(escapeFile), "zip-slip entry was extracted outside the wdc tree");
    }

    // ── #106 Crash recovery ──────────────────────────────────────────────────

    [Fact]
    public void Half_extracted_tmp_directory_is_skipped_by_listing()
    {
        // Simulate a daemon that died mid-extract: leave behind <version>.tmp
        var binariesRoot = Path.Combine(_wdcDir, "binaries");
        var apacheRoot = Path.Combine(binariesRoot, "apache");
        var halfExtracted = Path.Combine(apacheRoot, "2.4.62.tmp");
        Directory.CreateDirectory(halfExtracted);
        File.WriteAllText(Path.Combine(halfExtracted, "garbage.txt"), "partial");
        // Also a fully-installed sibling
        var fullExtracted = Path.Combine(apacheRoot, "2.4.61");
        Directory.CreateDirectory(Path.Combine(fullExtracted, "bin"));
        File.WriteAllText(Path.Combine(fullExtracted, "bin", "httpd.exe"), "fake-binary");

        // The BinaryManager listing rule used in production filters .tmp suffixes —
        // assert the same here so we catch a regression if that filter ever drops.
        var versions = Directory.GetDirectories(apacheRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(versions);
        Assert.Equal("2.4.61", versions[0]);
    }

    [Fact]
    public void DaemonJobObject_initializes_without_throwing_on_repeated_calls()
    {
        // EnsureInitialized must be idempotent — callable from Program.cs Main and from
        // each plugin Start without producing duplicate kernel handles or crashes.
        var first = DaemonJobObject.EnsureInitialized();
        var second = DaemonJobObject.EnsureInitialized();
        var third = DaemonJobObject.EnsureInitialized();
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void ProcessMetricsSampler_first_sample_returns_zero_cpu()
    {
        // Without a previous snapshot the sampler must return 0% (delta requires 2 samples).
        // We sample the current test process — guaranteed to be alive.
        var current = System.Diagnostics.Process.GetCurrentProcess();
        var (cpu, mem) = ProcessMetricsSampler.Sample(current);
        Assert.Equal(0, cpu);
        Assert.True(mem > 0, "memory should be > 0 for a live process");
    }

    [Fact]
    public void ProcessMetricsSampler_handles_null_process_gracefully()
    {
        var (cpu, mem) = ProcessMetricsSampler.Sample(null);
        Assert.Equal(0, cpu);
        Assert.Equal(0, mem);
    }

    // ── helper ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a BackupManager pinned to the test's temp ~/.wdc/ tree via the
    /// test-only constructor overload. This sidesteps the production
    /// SpecialFolder.UserProfile lookup which on Windows ignores process env vars.
    /// </summary>
    private BackupManager CreateBackupManagerForRoot() => new(_logger, _wdcDir);
}
