# C# / .NET Process Management — Technical Appendix

**Project**: DevForge  
**Date**: 2026-04-09  
**Scope**: Starting, stopping, restarting, and monitoring Apache, Nginx, MySQL, MariaDB, PHP-FPM, Redis, Node.js, dnsmasq, Mailpit across Windows, macOS, Linux.

---

## 1. System.Diagnostics.Process Patterns

### Starting a process and capturing the PID

```csharp
var psi = new ProcessStartInfo
{
    FileName = "nginx",
    Arguments = "",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
};

var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
process.Start();
int pid = process.Id; // valid immediately after Start()
```

### Real-time stdout/stderr with BeginOutputReadLine

```csharp
process.OutputDataReceived += (_, e) =>
{
    if (e.Data is not null)
        OnLogLine(pid, e.Data, isError: false);
};
process.ErrorDataReceived += (_, e) =>
{
    if (e.Data is not null)
        OnLogLine(pid, e.Data, isError: true);
};

process.Start();
process.BeginOutputReadLine();
process.BeginErrorReadLine();
```

`BeginOutputReadLine` posts callbacks on a `ThreadPool` thread. The callback is invoked with `null` when the stream closes — always guard against it.

### Detecting crashes via the Exited event

```csharp
process.EnableRaisingEvents = true;
process.Exited += (_, _) =>
{
    int exitCode = process.ExitCode;
    // exitCode == 0 → clean shutdown, != 0 → crash or forced kill
    _stateManager.NotifyExited(pid, exitCode);
};
```

`Exited` fires on a `ThreadPool` thread. The `Process` object may be disposed before your handler runs; capture `ExitCode` immediately.

### Process.Kill() vs graceful shutdown

| Method | What it does | When to use |
|--------|-------------|-------------|
| `process.Kill()` | `SIGKILL` (Unix) / `TerminateProcess` (Windows) — immediate | Last resort after timeout |
| `process.Kill(entireProcessTree: true)` | Kills the process and all children (.NET 5+) | Services that spawn workers |
| Send SIGTERM via `kill -TERM <pid>` | Cooperative shutdown on Unix | Preferred on Unix |
| Service-specific stop command | e.g., `nginx -s quit` | Always preferred |

### Cross-platform termination helper

```csharp
static async Task StopGracefullyAsync(Process process, TimeSpan timeout, CancellationToken ct)
{
    if (OperatingSystem.IsWindows())
    {
        // Windows has no SIGTERM; use service-specific stop command first (see §2)
        // Fall through to Kill after timeout
    }
    else
    {
        // Unix: send SIGTERM
        kill(process.Id, SIGTERM);
    }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(timeout);
    try
    {
        await process.WaitForExitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
    }
}

[DllImport("libc", SetLastError = true)]
private static extern int kill(int pid, int sig);
private const int SIGTERM = 15;
```

---

## 2. Graceful Shutdown Per Service

All timeouts below are recommendations; expose them as configurable settings.

| Service | Stop command | Timeout | Notes |
|---------|-------------|---------|-------|
| **Apache (Windows)** | `httpd.exe -k stop` | 30 s | Never `Kill()`; the service wrapper handles workers |
| **Apache (Unix)** | `apachectl graceful-stop` or `kill -TERM <pid>` | 30 s | `graceful-stop` waits for in-flight requests |
| **Nginx (graceful)** | `nginx -s quit` | 30 s | Drains connections |
| **Nginx (fast)** | `nginx -s stop` | immediate | |
| **MySQL / MariaDB** | `mysqladmin -u root [-p] shutdown` | 60 s | Flushes InnoDB buffer; `kill -TERM` also works |
| **PHP-FPM (graceful)** | `kill -QUIT <master_pid>` | 60 s | Waits for workers; use PID file |
| **PHP-FPM (fast)** | `kill -TERM <master_pid>` | 5 s | |
| **Redis** | `redis-cli [-a pass] shutdown` | 10 s | Persists RDB/AOF before exit |
| **Node.js** | `kill -TERM <pid>` | 15 s | App should handle SIGTERM |
| **dnsmasq** | `kill -TERM <pid>` | 5 s | |
| **Mailpit** | `kill -TERM <pid>` | 5 s | |

```csharp
// Example: MySQL graceful stop via CliWrap (see §4)
await Cli.Wrap("mysqladmin")
    .WithArguments(["--user=root", $"--password={password}", "shutdown"])
    .ExecuteAsync(ct);
```

**Timeout fallback pattern** (applies to all services):

```csharp
async Task StopServiceAsync(ServiceConfig svc, CancellationToken ct)
{
    await RunStopCommandAsync(svc, ct);                     // service-specific
    var exited = await WaitForExitAsync(svc.Pid, TimeSpan.FromSeconds(svc.GracefulTimeoutSecs), ct);
    if (!exited)
        Process.GetProcessById(svc.Pid).Kill(entireProcessTree: true);
}
```

---

## 3. Windows Job Objects

When DevForge exits unexpectedly, any child processes it launched must also die. `.Kill(entireProcessTree: true)` only works when you can call it. Job Objects enforce this at the OS level.

### P/Invoke definitions

```csharp
internal static class JobObjects
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(nint hJob, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags, MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit, Affinity, PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    const int JobObjectExtendedLimitInformation = 9;

    public static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObject(nint.Zero, null);
        if (job == nint.Zero) throw new Win32Exception();

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        uint size = (uint)Marshal.SizeOf(info);
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size))
            throw new Win32Exception();

        return job;
    }

    public static void AssignProcess(nint jobHandle, Process process)
    {
        if (!AssignProcessToJobObject(jobHandle, process.Handle))
            throw new Win32Exception();
    }
}
```

**Usage at startup:**

```csharp
// Store as a field — the job lives as long as this handle
nint _jobHandle = OperatingSystem.IsWindows()
    ? JobObjects.CreateKillOnCloseJob()
    : nint.Zero;

void StartService(ProcessStartInfo psi)
{
    var process = Process.Start(psi)!;
    if (OperatingSystem.IsWindows())
        JobObjects.AssignProcess(_jobHandle, process);
    // ...
}
```

`.NET 5+ Process.Kill(entireProcessTree: true)` is sufficient for **clean shutdowns** but does nothing if the DevForge process itself crashes. Job Objects handle the crash case on Windows; `prctl(PR_SET_PDEATHSIG, SIGTERM)` handles it on Linux.

---

## 4. CliWrap for One-Shot Commands

Use **CliWrap** (NuGet: `CliWrap`) for commands that run, produce output, and exit. Use `System.Diagnostics.Process` for long-lived daemons you need to monitor.

```
dotnet add package CliWrap
```

### Config validation

```csharp
var result = await Cli.Wrap("httpd")
    .WithArguments(["-t", "-f", configPath])
    .WithValidation(CommandResultValidation.None)
    .ExecuteBufferedAsync(ct);

if (result.ExitCode != 0)
    throw new ConfigValidationException(result.StandardError);
```

### TLS certificate generation with mkcert

```csharp
await Cli.Wrap("mkcert")
    .WithArguments(["-cert-file", certFile, "-key-file", keyFile, domain, $"*.{domain}"])
    .ExecuteAsync(ct);
```

### Database creation

```csharp
await Cli.Wrap("mysqladmin")
    .WithArguments(["--user=root", $"--password={password}", "create", dbName])
    .ExecuteAsync(ct);
```

### Streaming output from CliWrap

```csharp
await Cli.Wrap("php")
    .WithArguments(["--version"])
    .WithStandardOutputPipe(PipeTarget.ToDelegate(line => Console.WriteLine(line)))
    .ExecuteAsync(ct);
```

CliWrap throws `CommandExecutionException` on non-zero exit codes (with `ZeroExitCode` validation). Use `.WithValidation(CommandResultValidation.None)` when non-zero is expected (e.g., `httpd -t` warnings).

---

## 5. Health Check Patterns

### TCP port check

```csharp
static async Task<bool> IsTcpPortOpenAsync(int port, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        return true;
    }
    catch { return false; }
}
```

### HTTP health check

```csharp
static async Task<bool> IsHttpHealthyAsync(string url, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        var resp = await _httpClient.GetAsync(url, cts.Token);
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}
```

### MySQL ping

```csharp
static async Task<bool> IsMysqlAliveAsync(string password, CancellationToken ct)
{
    var result = await Cli.Wrap("mysqladmin")
        .WithArguments(["--user=root", $"--password={password}", "ping"])
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync(ct);
    return result.ExitCode == 0 && result.StandardOutput.Contains("alive");
}
```

### PHP-FPM status page

Configure `pm.status_path = /fpm-status` in the pool config, then:

```csharp
var result = await _httpClient.GetStringAsync($"http://127.0.0.1:{fpmPort}/fpm-status?json", ct);
var doc = JsonDocument.Parse(result);
bool accepting = doc.RootElement.GetProperty("accepted conn").GetInt64() >= 0;
```

### Polling with exponential backoff and auto-restart

```csharp
async Task MonitorAsync(ServiceState svc, CancellationToken ct)
{
    int failures = 0;
    while (!ct.IsCancellationRequested)
    {
        bool healthy = await CheckHealthAsync(svc, ct);
        if (healthy)
        {
            failures = 0;
            await Task.Delay(svc.PollInterval, ct);
            continue;
        }

        failures++;
        var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures), 60));
        _logger.LogWarning("{Service} unhealthy (attempt {N}), restart in {Delay}s", svc.Name, failures, backoff.TotalSeconds);
        await Task.Delay(backoff, ct);

        if (failures <= svc.MaxRestarts)
            await StartServiceAsync(svc, ct);
        else
        {
            _logger.LogError("{Service} exceeded max restarts — giving up", svc.Name);
            break;
        }
    }
}
```

---

## 6. Real-Time Log Streaming

### Process stdout → gRPC server-side stream

```csharp
// gRPC server method
public override async Task StreamLogs(LogRequest req,
    IServerStreamWriter<LogLine> stream, ServerCallContext ctx)
{
    var channel = _logBroker.Subscribe(req.ServiceId);
    await foreach (var line in channel.ReadAllAsync(ctx.CancellationToken))
        await stream.WriteAsync(new LogLine { Text = line.Text, IsError = line.IsError });
}
```

```csharp
// Feeding the channel from process output
process.OutputDataReceived += (_, e) =>
{
    if (e.Data is not null)
        _logBroker.Publish(serviceId, new LogEntry(e.Data, false));
};
process.BeginOutputReadLine();
```

`Channel<T>` (`System.Threading.Channels`) is the appropriate broker: bounded with `BoundedChannelFullMode.DropOldest` at ~2000 entries keeps memory bounded without blocking the reader callback.

### Performance at 1000 lines/sec

- `BeginOutputReadLine` delivers lines via `ThreadPool`; the callback must be non-blocking.
- Avoid `string.Format` / `JsonSerializer` in the hot path — write raw lines, format in the consumer.
- For the gRPC stream, batch lines if the client is slow: accumulate for 16 ms, then write a batch message.

### Log rotation

Services write their own log files. DevForge should not rotate them; instead, configure `logrotate` (Unix) or Windows Event Log redirect at the service level. DevForge only tails live stdout.

---

## 7. Cross-Platform Abstraction

### Interface

```csharp
public interface IProcessManager
{
    Task<RunningService> StartAsync(ServiceConfig config, CancellationToken ct);
    Task StopAsync(RunningService service, CancellationToken ct);
    Task RestartAsync(RunningService service, CancellationToken ct);
    Task<bool> IsRunningAsync(RunningService service, CancellationToken ct);
}
```

### Platform detection and DI registration

```csharp
// In Program.cs / Startup
if (OperatingSystem.IsWindows())
    services.AddSingleton<IProcessManager, WindowsProcessManager>();
else
    services.AddSingleton<IProcessManager, UnixProcessManager>();
```

### UnixProcessManager — SIGTERM helper

```csharp
public class UnixProcessManager : IProcessManager
{
    [DllImport("libc")] static extern int kill(int pid, int sig);

    public async Task StopAsync(RunningService svc, CancellationToken ct)
    {
        await RunServiceStopCommandAsync(svc, ct);   // nginx -s quit, etc.
        bool exited = await WaitForExitAsync(svc.Pid, svc.Config.GracefulTimeout, ct);
        if (!exited)
            Process.GetProcessById(svc.Pid).Kill(entireProcessTree: true);
    }
}
```

### Conditional compilation

Reserve `#if` for platform-specific P/Invoke declarations, not for business logic. Prefer runtime `OperatingSystem.IsWindows()` checks — they are tree-shaken by the JIT on non-Windows targets and keep the code readable.

```csharp
#if WINDOWS
[DllImport("kernel32.dll")] ...
#endif
```

---

## 8. PHP-FPM on Windows

PHP-FPM is a Unix-only binary. On Windows, DevForge uses **php-cgi.exe** as the FastCGI backend.

### Architecture

```
Browser → Apache → mod_proxy_fcgi → php-cgi.exe (TCP 9000)
```

`php-cgi.exe` must be started with `PHP_FCGI_CHILDREN` and `PHP_FCGI_MAX_REQUESTS` environment variables:

```csharp
var psi = new ProcessStartInfo
{
    FileName = phpCgiPath,          // e.g., C:\php\8.2\php-cgi.exe
    UseShellExecute = false,
    CreateNoWindow = true,
    Environment =
    {
        ["PHP_FCGI_CHILDREN"] = "4",
        ["PHP_FCGI_MAX_REQUESTS"] = "500",
        ["PHPRC"] = phpIniDir,
    }
};
// php-cgi.exe listens on FCGI_PORT env var or defaults to stdin — bind via Apache
```

### Apache virtual host template differences

**Unix (FPM socket)**

```apache
<FilesMatch \.php$>
    SetHandler "proxy:unix:/run/php/php8.2-fpm.sock|fcgi://localhost"
</FilesMatch>
```

**Windows (php-cgi TCP)**

```apache
<FilesMatch \.php$>
    SetHandler "proxy:fcgi://127.0.0.1:9082"
</FilesMatch>
```

DevForge template engine must select the correct snippet based on `OperatingSystem.IsWindows()`. Assign each PHP version a deterministic port (e.g., 9056 for 5.6, 9074 for 7.4, 9082 for 8.2) to allow multiple versions to coexist.

### Stopping php-cgi on Windows

`php-cgi.exe` does not respond to `CTRL_C_EVENT` reliably. Use `Process.Kill(entireProcessTree: true)` after a short delay, or track the PID and use `taskkill /F /PID`.

```csharp
public async Task StopPhpCgiAsync(RunningService svc, CancellationToken ct)
{
    // php-cgi has no graceful stop; give in-flight requests ~5 s then force
    await Task.Delay(TimeSpan.FromSeconds(5), ct);
    var process = Process.GetProcessById(svc.Pid);
    process.Kill(entireProcessTree: true);
}
```

---

## Summary Reference Table

| Service | Start binary | Graceful stop | Health check | Windows note |
|---------|-------------|--------------|--------------|--------------|
| Apache | `httpd` | `httpd -k stop` / `apachectl graceful-stop` | TCP :80 | Run as service or directly |
| Nginx | `nginx` | `nginx -s quit` | TCP :80 | No service wrapper needed |
| MySQL | `mysqld` | `mysqladmin shutdown` | `mysqladmin ping` | Use `mysqld --standalone` |
| MariaDB | `mariadbd` | `mysqladmin shutdown` | `mysqladmin ping` | Same as MySQL |
| PHP-FPM | `php-fpm` | `kill -QUIT <pid>` | status page | Use php-cgi.exe on Windows |
| Redis | `redis-server` | `redis-cli shutdown` | TCP :6379 | Windows: use redis-server.exe |
| Node.js | `node` | SIGTERM | TCP check | Process.Kill fallback |
| dnsmasq | `dnsmasq` | SIGTERM | UDP :53 query | Not available on Windows |
| Mailpit | `mailpit` | SIGTERM | HTTP /api/v1/info | Cross-platform binary |
