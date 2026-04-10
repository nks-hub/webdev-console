namespace NKS.WebDevConsole.Core.Models;

public record ServiceStatus(
    ServiceState State, int? Pid, TimeSpan Uptime, int RestartCount,
    double CpuPercent, long MemoryBytes);

public record ValidationResult(bool IsValid, string? ErrorMessage = null);
