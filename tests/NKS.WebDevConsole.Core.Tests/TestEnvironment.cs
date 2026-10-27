using System.Runtime.CompilerServices;

namespace NKS.WebDevConsole.Core.Tests;

/// <summary>Process-wide settings for the test run, applied before any test executes.</summary>
internal static class TestEnvironment
{
    /// <summary>
    /// Points WdcPaths at a per-process temp folder so no test writes into the
    /// developer's real ~/.wdc.
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WDC_DATA_DIR"))) return;
        var root = Path.Combine(Path.GetTempPath(), $"nks-wdc-tests-{Environment.ProcessId}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("WDC_DATA_DIR", root);
    }
}
