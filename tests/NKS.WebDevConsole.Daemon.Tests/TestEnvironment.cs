using System.Runtime.CompilerServices;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Process-wide settings for the test run, applied before any test executes.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init()
    {
        // SiteOrchestrator.ApplyAsync rewrites the WDC block of the real system
        // hosts file. A test applying one fake site once replaced every local
        // domain with that single entry, so tests must never reach that write.
        Environment.SetEnvironmentVariable("NKS_WDC_SKIP_HOSTS_UAC", "1");
        UseTempDataRoot();
    }

    /// <summary>
    /// Points WdcPaths at a per-process temp folder. Without it, tests that use
    /// the default data root wrote into the developer's real ~/.wdc (state files,
    /// php-extensions.json), and parallel runs polluted each other.
    /// </summary>
    internal static void UseTempDataRoot()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WDC_DATA_DIR"))) return;
        var root = Path.Combine(Path.GetTempPath(), $"nks-wdc-tests-{Environment.ProcessId}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("WDC_DATA_DIR", root);
    }
}
