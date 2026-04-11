using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Services;

/// <summary>
/// Pre-registers Windows Defender Firewall inbound rules for NKS WDC managed
/// service ports so the user doesn't get the per-first-bind UAC prompt each
/// time a managed binary (Apache, MySQL, Redis, Mailpit) opens a socket for
/// the first time.
///
/// Rule naming convention:
///   <c>NKS.WebDevConsole.{service}.{port}</c> — e.g. <c>NKS.WebDevConsole.apache.80</c>
///
/// Each rule is:
///   - direction: in
///   - action: allow
///   - protocol: TCP
///   - localport: the service's listen port
///   - profile: private (trusted LAN; never public — matches dev-on-laptop use case)
///
/// Idempotent: <c>netsh advfirewall firewall show rule name="X"</c> is queried
/// first and registration is skipped when the rule already exists. Silent
/// failure on missing admin privileges — logs a warning and carries on so
/// the daemon startup stays non-blocking (per the project's "no UAC loops"
/// rule). Users without admin can still run the managed services; they'll
/// just see the Windows "Allow access" dialog on first bind exactly like
/// before this service existed.
/// </summary>
public sealed class WindowsFirewallManager
{
    private readonly ILogger<WindowsFirewallManager> _logger;

    /// <summary>
    /// Ports managed by the daemon that need inbound firewall rules.
    /// PHP-CGI is intentionally absent — mod_fcgid only speaks localhost to
    /// Apache, never accepts inbound connections.
    /// </summary>
    private static readonly (string Service, int Port)[] ManagedPorts =
    {
        ("apache-http", 80),
        ("apache-https", 443),
        ("mysql", 3306),
        ("redis", 6379),
        ("mailpit-smtp", 1025),
        ("mailpit-web", 8025),
    };

    public WindowsFirewallManager(ILogger<WindowsFirewallManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers firewall rules for all managed ports. Runs once on daemon
    /// startup. No-op on non-Windows; no-op when rules already exist.
    /// Returns the number of rules actually created (0 if already present).
    /// </summary>
    public async Task<int> EnsureRulesRegisteredAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogDebug("Not Windows — skipping firewall rule registration");
            return 0;
        }

        // Cheap pre-check: without admin we can still *query* rules, but can't
        // create them. Skip the mutation attempts entirely when not elevated.
        if (!IsRunningAsAdmin())
        {
            _logger.LogInformation(
                "Not running as admin — skipping firewall rule registration. " +
                "Users will see the Windows Defender Firewall prompt on first bind for each managed service.");
            return 0;
        }

        int created = 0;
        foreach (var (service, port) in ManagedPorts)
        {
            var ruleName = $"NKS.WebDevConsole.{service}.{port}";
            try
            {
                if (await RuleExistsAsync(ruleName, ct))
                {
                    _logger.LogDebug("Firewall rule {Rule} already exists — skipping", ruleName);
                    continue;
                }
                if (await AddRuleAsync(ruleName, port, ct))
                {
                    _logger.LogInformation("Registered firewall rule {Rule} (TCP:{Port})", ruleName, port);
                    created++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register firewall rule {Rule} — continuing", ruleName);
            }
        }
        return created;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task<bool> RuleExistsAsync(string ruleName, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            ArgumentList =
            {
                "advfirewall", "firewall", "show", "rule",
                $"name={ruleName}",
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        await p.WaitForExitAsync(ct);
        // Exit code 0 means the rule was found. Exit code 1 means
        // "No rules match the specified criteria." — both are expected.
        return p.ExitCode == 0;
    }

    private async Task<bool> AddRuleAsync(string ruleName, int port, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            ArgumentList =
            {
                "advfirewall", "firewall", "add", "rule",
                $"name={ruleName}",
                "dir=in",
                "action=allow",
                "protocol=TCP",
                $"localport={port}",
                "profile=private",
                $"description=NKS WebDev Console managed service (auto-registered). Delete via netsh advfirewall firewall delete rule name=\"{ruleName}\".",
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync(ct);
            _logger.LogWarning("netsh add rule {Rule} exited {Code}: {Err}", ruleName, p.ExitCode, err.Trim());
            return false;
        }
        return true;
    }
}
