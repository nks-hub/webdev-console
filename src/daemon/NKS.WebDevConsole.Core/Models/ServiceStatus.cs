namespace NKS.WebDevConsole.Core.Models;

public record ServiceStatus(
    string Id, string DisplayName,
    ServiceState State, int? Pid,
    double CpuPercent, long MemoryBytes,
    TimeSpan? Uptime);

public record ValidationResult(bool IsValid, string? ErrorMessage = null);
