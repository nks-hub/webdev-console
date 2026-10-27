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
    }
}
