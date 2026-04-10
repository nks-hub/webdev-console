# Avalonia Ecosystem Research — DevForge

Date: 2026-04-09
Scope: NuGet packages, API patterns, gotchas for DevForge stack

---

## 1. Avalonia UI 12.x

### Current state

Avalonia 12.0.0 shipped on 2025-04-07. It is the first release to drop .NET Framework and .NET Standard entirely. Minimum runtime is .NET 8; recommended target is .NET 10.

**Key NuGet packages**

| Package | Version | Notes |
|---|---|---|
| `Avalonia` | `12.0.0` | Core framework |
| `Avalonia.Desktop` | `12.0.0` | Windows/macOS/Linux desktop host |
| `Avalonia.Controls.DataGrid` | `12.0.0` | Separate package, must add explicitly |
| `Avalonia.Themes.Fluent` | `12.0.0` | Fluent theme — no longer bundled in core |
| `Avalonia.Diagnostics` | **removed** | Replaced by `AvaloniaUI.DiagnosticsSupport` (Avalonia Plus) |

### FluentTheme dark/light switching

The old `Mode` property is gone. Use `Application.RequestedThemeVariant` instead:

```xml
<!-- App.axaml -->
<Application xmlns:themes="clr-namespace:FluentAvalonia.UI.Theming;assembly=FluentAvalonia"
             RequestedThemeVariant="Dark">
```

Programmatic switch at runtime:
```csharp
Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
// or ThemeVariant.Light, ThemeVariant.Default (follows OS)
```

`ThemeVariant.Default` follows the OS theme. FluentTheme only supports Dark and Light; custom palette variants are not possible with FluentTheme alone.

### TrayIcon API

`TrayIcon` must be declared in App.axaml, not in a Window. It uses `NativeMenu`, not the Avalonia `Menu` control.

```xml
<Application.DataTemplates> ... </Application.DataTemplates>

<TrayIcon.Icons>
    <TrayIcons>
        <TrayIcon Icon="/Assets/tray.ico" ToolTipText="DevForge">
            <TrayIcon.Menu>
                <NativeMenu>
                    <NativeMenuItem Header="Open" Command="{Binding OpenCommand}"/>
                    <NativeMenuItemSeparator/>
                    <NativeMenuItem Header="Quit" Command="{Binding QuitCommand}"/>
                </NativeMenu>
            </TrayIcon.Menu>
        </TrayIcon>
    </TrayIcons>
</TrayIcon.Icons>
```

**Cross-platform caveats:**
- Windows: works fully
- macOS: works fully
- Linux: confirmed on Ubuntu (GNOME); varies heavily by desktop environment — KDE Plasma / Wayland may not show the tray at all
- Icon must be added as `<AvaloniaResource>` in .csproj

### DataGrid vs ItemsControl for site list

Recommendation: use `ListBox` with a custom `DataTemplate` for a site list. Reasons:
- `DataGrid` is in a separate NuGet (`Avalonia.Controls.DataGrid`) and is primarily an editable-grid control; overkill for a read-only list
- `TreeDataGrid` (also separate package) offers better virtualization for large sorted/grouped lists
- `ListBox` with compiled bindings (default in 12.x) is fast and straightforward
- For plain tabular data without editing, `TreeDataGrid` outperforms `DataGrid`

If column-based layout is required, prefer `TreeDataGrid` (`Avalonia.Controls.TreeDataGrid`).

### Hot reload (2026 status)

Official XAML hot reload is **not available** in the open-source build. The Avalonia team has stated it will be a paid Accelerate feature if implemented. Community alternatives:
- **HotAvalonia** (`HotAvalonia`, v3.1.0) — IDE-agnostic, supports Windows/macOS/Linux, works in Rider and VS Code. Dev-only dependency.
- **Live.Avalonia** — older, experimental

For DevForge: add `HotAvalonia` as a conditional dev package.

### Native AOT

Avalonia 12 supports Native AOT. XAML is compiled at build time; reflection-based bindings must be replaced with compiled bindings (now the default). AOT build:
```bash
dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true
```
Limitation: third-party controls that rely on reflection (some older community packages) may break under AOT.

**Gotcha:** `Avalonia.Diagnostics` was removed from open-source in 12.x. Do not reference it; use `AvaloniaUI.DiagnosticsSupport` through Avalonia Plus subscription, or ship without in production.

---

## 2. gRPC in .NET

### Package choice

| Scenario | Package |
|---|---|
| Server (hosted in daemon/worker) | `Grpc.AspNetCore` |
| Client only (GUI process) | `Grpc.Net.Client` |
| Code generation from .proto | `Grpc.Tools` (auto-added via `Grpc.AspNetCore`) |
| Shared .proto contracts | Separate `DevForge.Contracts` project |

Do **not** use `GrpcDotNetNamedPipes` — it uses a custom wire protocol incompatible with standard gRPC tooling.

### IPC transport recommendation

Use **Unix Domain Sockets (UDS)** on macOS/Linux and **Named Pipes** on Windows, with OS detection at startup:

```csharp
builder.WebHost.ConfigureKestrel(opts =>
{
    if (OperatingSystem.IsWindows())
        opts.ListenNamedPipe("devforge-daemon");
    else
    {
        var sock = Path.Combine(Path.GetTempPath(), "devforge.sock");
        if (File.Exists(sock)) File.Delete(sock);
        opts.ListenUnixSocket(sock);
    }
    opts.ConfigureEndpointDefaults(o => o.Protocols = HttpProtocols.Http2);
});
```

UDS is the preferred cross-platform choice; named pipes have the advantage of Windows ACL security integration.

### Server-side streaming for real-time logs

```protobuf
service DaemonService {
    rpc StreamLogs(LogRequest) returns (stream LogEntry);
}
```

Client consumption:
```csharp
using var call = client.StreamLogs(new LogRequest { ServiceId = "apache" });
await foreach (var entry in call.ResponseStream.ReadAllAsync(ct))
    AppendLine(entry.Message);
```

### Error handling

Use gRPC interceptors on the server for centralized handling:
```csharp
public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
    TRequest request, ServerCallContext context,
    UnaryServerMethod<TRequest, TResponse> continuation)
{
    try { return await continuation(request, context); }
    catch (Exception ex)
    {
        throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
}
```

For streaming, catch exceptions inside the `while`/`foreach` loop and complete the stream gracefully; the client receives `RpcException` with the status code.

### Proto project structure

```
DevForge.Contracts/
  Protos/
    daemon.proto
    sites.proto
  DevForge.Contracts.csproj  ← Grpc.Tools, Protobuf items
```

Both `DevForge.Daemon` and `DevForge.GUI` reference `DevForge.Contracts`. GUI only generates client stubs: `GrpcServices="Client"`.

---

## 3. Process Management in C#

### Core API

`System.Diagnostics.Process` is sufficient for launching and monitoring Apache/MySQL/PHP-FPM. Key settings:

```csharp
var psi = new ProcessStartInfo("httpd")
{
    Arguments = "-f /etc/httpd/httpd.conf",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};
var proc = Process.Start(psi)!;
proc.BeginOutputReadLine();
proc.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
```

Use `BeginOutputReadLine` / `BeginErrorReadLine` (async event-based) rather than `ReadToEnd` to avoid deadlocks on large output.

### PID tracking

`proc.Id` gives the PID immediately after `Start()`. Persist to a file or in-memory dictionary. On restart, check if the PID is still live before attempting kill:
```csharp
bool isRunning = !proc.HasExited;
// or for a stored PID:
Process.GetProcessById(pid); // throws ArgumentException if gone
```

### Process tree kill on Windows

Since .NET 5: `process.Kill(entireProcessTree: true)` kills the process and all descendants. This is the correct solution — no P/Invoke needed for the basic case.

For guaranteeing child cleanup when the parent crashes (daemon killed unexpectedly), use **Windows Job Objects** via P/Invoke. When the daemon registers itself and its children in a job, all children are terminated if the job handle closes:
```csharp
[DllImport("kernel32.dll")]
static extern IntPtr CreateJobObject(IntPtr a, string name);
[DllImport("kernel32.dll")]
static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
// Set JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE via SetInformationJobObject
```

On Linux/macOS, use process groups: `kill(-pgid, SIGTERM)` via `Mono.Posix.NETStandard` or a shell wrapper. Alternatively, `process.Kill(true)` handles this cross-platform.

### Graceful shutdown

- Windows: no `SIGTERM`. Best effort: `process.CloseMainWindow()` (sends WM_CLOSE to GUI apps), then `process.Kill()` after timeout
- Apache/MySQL respond to `SIGTERM` on Unix; on Windows, use their own `--stop` CLI flag
- Recommended pattern: send stop signal → wait 5s → `Kill(true)` fallback

### CliWrap (alternative for simpler usage)

`CliWrap` (v3.x) provides a fluent API over `System.Diagnostics.Process` with built-in pipe support and cancellation. Useful for one-shot commands; for long-running daemons, raw `Process` gives more control.

---

## 4. LiveCharts2 for Avalonia

**NuGet package:** `LiveChartsCore.SkiaSharpView.Avalonia` — latest stable: `2.0.0` (2026-03-29)

Avalonia version dependency: `>= 11.0.0`. Tested against Avalonia 11; **Avalonia 12 compatibility is unconfirmed in stable 2.0.0**. A prerelease `2.1.0-dev-*` is under active development and likely targets 12.x — monitor the repository before locking versions.

### Real-time chart pattern

```csharp
// ViewModel
public ObservableCollection<ObservableValue> CpuSeries { get; } = new();
public ISeries[] Series { get; }

public CpuViewModel()
{
    Series = new ISeries[]
    {
        new LineSeries<ObservableValue>
        {
            Values = CpuSeries,
            GeometrySize = 0,
            LineSmoothness = 0.3
        }
    };
}

// Update loop (every 500ms)
void Tick(double cpuPercent)
{
    if (CpuSeries.Count > 120) CpuSeries.RemoveAt(0);
    CpuSeries.Add(new ObservableValue(cpuPercent));
}
```

### Performance notes

LiveCharts2 uses a virtualization algorithm that builds a downsampled representation internally — the visual element count stays bounded regardless of data volume. For CPU/RAM monitoring with ~120 data points at 2Hz, performance is fine. At 60fps with thousands of points, it degrades; use a fixed-size circular buffer (remove oldest, add newest).

### Theme integration

```csharp
// In App.axaml.cs, after theme is applied:
LiveCharts.Configure(config =>
    config.AddDarkTheme()); // or AddLightTheme()
// Or bind to Application.Current.RequestedThemeVariant and reconfigure on change
```

**Gotcha:** LiveCharts2 2.0.0 has no built-in Avalonia 12 dark/light auto-sync; wire it manually via a theme-change subscription.

**Alternative if Avalonia 12 compat is blocked:** `OxyPlot.Avalonia` — leaner, no SkiaSharp dependency, less visual polish.

---

## 5. Plugin System

### Recommended: AssemblyLoadContext + DI

MEF is in **maintenance mode** (security fixes only, no new features) and does not integrate cleanly with `Microsoft.Extensions.DependencyInjection`. Do not use MEF for new work.

```csharp
public interface IDevForgePlugin
{
    string Name { get; }
    void Configure(IServiceCollection services);
}

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName name)
    {
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
```

Discovery:
```csharp
foreach (var dll in Directory.GetFiles(pluginsDir, "*.Plugin.dll"))
{
    var ctx = new PluginLoadContext(dll);
    var asm = ctx.LoadFromAssemblyPath(dll);
    var pluginType = asm.GetExportedTypes()
        .FirstOrDefault(t => typeof(IDevForgePlugin).IsAssignableFrom(t) && !t.IsAbstract);
    if (pluginType != null)
        services.AddSingleton(typeof(IDevForgePlugin), Activator.CreateInstance(pluginType)!);
}
```

### Hot-reload (unload + reload)

Only possible with `isCollectible: true`. Set all references to the context to null and call GC:
```csharp
weakRef = new WeakReference(loadContext);
loadContext = null;
GC.Collect();
GC.WaitForPendingFinalizers();
// weakRef.IsAlive == false when fully unloaded
```
Requires that no loaded types are still referenced from the main context — the most common gotcha.

### Security

`AssemblyLoadContext` provides **no sandbox**. A loaded plugin can call any .NET API. Options:
- Code-sign plugin DLLs and verify signature before loading
- Run untrusted plugins in a separate process communicating via gRPC (strong isolation)
- For DevForge: plugins are first-party, so signature verification is sufficient

### Alternative library

`McMaster.NETCore.Plugins` (`Natemcmaster.DotNetCorePlugins`) provides a higher-level `PluginLoader` abstraction over `AssemblyLoadContext` with shared-type handling. Useful if shared interface types between host and plugin cause the common `InvalidCastException` problem with separate load contexts.

---

## 6. SQLite in .NET

### Package recommendation

Use **`Microsoft.Data.Sqlite`** (v9.x) directly with **`Dapper`** (v2.x).

| Option | Verdict |
|---|---|
| `Microsoft.Data.Sqlite` | Modern, lightweight, Microsoft-maintained, NuGet-native |
| `System.Data.SQLite` | Legacy, heavier, includes native binaries per platform |
| EF Core + SQLite | Overkill for a tool with simple schema; migration overhead |

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.*" />
<PackageReference Include="Dapper" Version="2.1.*" />
```

### WAL mode

Enable on first connection open:
```csharp
using var conn = new SqliteConnection("Data Source=devforge.db");
conn.Open();
conn.Execute("PRAGMA journal_mode=WAL;");
conn.Execute("PRAGMA synchronous=NORMAL;");
```

WAL allows one writer + multiple concurrent readers without blocking. Critical for daemon (writer) + GUI (reader) concurrent access.

**Gotcha:** Dapper does not handle `Guid`, `DateTimeOffset`, or `TimeSpan` automatically with `Microsoft.Data.Sqlite` — add type handlers:
```csharp
SqlMapper.AddTypeHandler(new GuidTypeHandler());
```

### Migration

Use **DbUp** (`dbup-sqlite`, v5.x) for simple embedded SQL script migrations:
```csharp
var upgrader = DeployChanges.To.SQLiteDatabase(connectionString)
    .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
    .LogToConsole()
    .Build();
upgrader.PerformUpgrade();
```

Scripts named `0001_initial.sql`, `0002_add_services.sql` etc. — DbUp tracks executed scripts in `SchemaVersions` table.

Use **FluentMigrator** only if rollback or strongly-typed C# migrations are required. For DevForge's embedded SQLite, DbUp's raw SQL approach is simpler and requires no runner configuration.

---

## 7. CLI in .NET

### Package choice

| Package | Version | Status |
|---|---|---|
| `System.CommandLine` | `2.0.0-beta5` | Beta, targeting stable with .NET 10 (late 2025). Many breaking changes between betas. |
| `Spectre.Console` | `0.55.0` | Stable, rich output (tables, progress, colors) |
| `Spectre.Console.Cli` | `0.55.0` | Available but **NOT used** — see note below |

**NOTE (OVERRIDDEN BY SPEC.md):** Original recommendation was `Spectre.Console.Cli`. However, `System.CommandLine` 2.0.5 shipped STABLE (March 2026). **SPEC.md decision: `System.CommandLine` for command parsing, `Spectre.Console` for output formatting ONLY. Do NOT use `Spectre.Console.Cli`.**

```csharp
var app = new CommandApp();
app.Configure(config =>
{
    config.AddCommand<StartCommand>("start");
    config.AddCommand<StopCommand>("stop");
    config.AddCommand<StatusCommand>("status");
});
return app.Run(args);
```

### Sharing gRPC client between CLI and GUI

Both CLI (`DevForge.CLI`) and GUI (`DevForge.GUI`) reference `DevForge.Contracts` and create a `GrpcChannel` to the daemon socket. Extract a `DaemonClient` service class into a shared `DevForge.Client` library used by both:

```
DevForge.Client/
  DaemonClient.cs   ← wraps GrpcChannel, exposes typed methods
DevForge.CLI/       ← references DevForge.Client
DevForge.GUI/       ← references DevForge.Client
```

---

## 8. Installer / Deployment

### Velopack (recommended for auto-updates)

**NuGet:** `Velopack` (v0.0.1298+)
**CLI tool:** `vpk` (installs via `dotnet tool install -g vpk`)

Velopack has a Rust-based update engine and a C# library. Generates installer EXE, delta packages, and a self-updating portable package from one command.

```csharp
// Entry point — must be first lines before any Avalonia init
VelopackApp.Build().Run();
```

```bash
# Package after dotnet publish
dotnet publish -c Release -r win-x64 --self-contained -o publish/
vpk pack --packId DevForge --packVersion 1.0.0 --packDir publish/ --mainExe DevForge.exe
```

Updates are applied in ~2 seconds, no UAC required for user-scope installs. Upload the `releases/` output to GitHub Releases or S3.

**Supported platforms:** Windows (NSIS-based EXE), macOS (DMG), Linux (AppImage).

### Inno Setup vs WiX v4

- **Inno Setup 6.x**: simple EXE installer, Pascal scripting, no MSI output. Best for simple user installs.
- **WiX v4 / v6** (v6 released 2025-04-07): MSI/MSIX output, required for enterprise GPO deployment. Steeper learning curve, XML-heavy.

For DevForge: **Velopack wraps Inno Setup** for Windows EXE generation — use Velopack as the primary path; fall back to WiX only if an MSI is contractually required.

### `dotnet publish` flags (non-trimmed self-contained)

```bash
dotnet publish -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:ReadyToRun=true
  # Omit PublishTrimmed=true to avoid reflection breaks in Avalonia/plugins
```

`ReadyToRun` gives partial AOT (R2R) startup improvement without full trim. Do not enable `PublishAot` unless all dependencies are AOT-compatible.

---

## 9. Template Engine for Config Generation

### Recommendation: Scriban

**NuGet:** `Scriban` (v7.0.6, actively maintained)

Scriban is 5x faster than DotLiquid in benchmarks, supports both Scriban and Liquid syntax, is AOT/trim-friendly on .NET 8+, and has async rendering. RazorLight's last commit was 2023; DotLiquid's was 2021 — both are effectively unmaintained.

Apache `VirtualHost` template example:
```
<VirtualHost *:{{ port }}>
    ServerName {{ site.domain }}
    DocumentRoot "{{ site.root }}"
    {{ if site.ssl }}
    SSLEngine on
    SSLCertificateFile "{{ site.cert_path }}"
    {{ end }}
</VirtualHost>
```

```csharp
var template = Template.Parse(templateText);
var result = await template.RenderAsync(new { site, port });
```

Scriban sandboxes by default — the template cannot call arbitrary .NET methods unless you explicitly expose them, which is a security benefit for user-editable templates.

---

## 10. Cross-Platform Considerations

### macOS: no universal binary from `dotnet publish`

.NET SDK does not produce a universal binary (fat binary containing both arm64 + x64) in a single `dotnet publish` call. The GitHub issue tracking this has been open since .NET 7 with no resolution. Workaround:
```bash
dotnet publish -r osx-arm64 -o publish/arm64/
dotnet publish -r osx-x64 -o publish/x64/
lipo -create publish/arm64/DevForge publish/x64/DevForge -output publish/DevForge
```
`lipo` is macOS-only; automate in CI. Velopack handles this per-arch and generates separate DMGs.

### Linux: AppImage packaging

No built-in .NET tooling for AppImage. Use `appimagetool` after `dotnet publish -r linux-x64 --self-contained`. Velopack automates this via `vpk pack` for Linux targets.

### Process management differences

| Concern | Windows | Linux/macOS |
|---|---|---|
| Graceful stop | `CloseMainWindow()` → `Kill()` | `process.Kill()` sends SIGTERM; processes that handle it shut down cleanly |
| Kill tree | `process.Kill(true)` (.NET 5+) | `process.Kill(true)` works; internally uses SIGKILL on the process group |
| SIGTERM equivalent | None (use process-specific stop mechanism) | `process.Kill()` defaults to SIGTERM in .NET on Unix |

**Note:** `Process.Kill()` on .NET on Unix sends SIGKILL (immediate), not SIGTERM. To send SIGTERM gracefully, use `Mono.Posix.NETStandard` (`Syscall.kill(pid, Signum.SIGTERM)`) or write a small native helper.

### File paths

Always use `Path.Combine` and `Path.DirectorySeparatorChar`. Never hardcode `/` or `\`. For config directories:
```csharp
var configDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "DevForge");
```

### mkcert

mkcert distributes pre-built binaries for Windows (x64/arm64), macOS (arm64/x64), and Linux (x64/arm64/arm). The same installation logic works on all platforms — download the correct binary for `RuntimeInformation.OSArchitecture` and run it.

---

## Quick NuGet Reference

```xml
<!-- Core UI -->
<PackageReference Include="Avalonia" Version="12.0.0" />
<PackageReference Include="Avalonia.Desktop" Version="12.0.0" />
<PackageReference Include="Avalonia.Themes.Fluent" Version="12.0.0" />
<PackageReference Include="Avalonia.Controls.DataGrid" Version="12.0.0" />

<!-- Dev-only hot reload -->
<PackageReference Include="HotAvalonia" Version="3.1.0" Condition="'$(Configuration)'=='Debug'" />

<!-- gRPC -->
<PackageReference Include="Grpc.AspNetCore" Version="2.67.*" />    <!-- daemon -->
<PackageReference Include="Grpc.Net.Client" Version="2.67.*" />    <!-- GUI/CLI -->

<!-- Charts -->
<PackageReference Include="LiveChartsCore.SkiaSharpView.Avalonia" Version="2.0.0" />

<!-- SQLite -->
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.*" />
<PackageReference Include="Dapper" Version="2.1.*" />
<PackageReference Include="dbup-sqlite" Version="5.0.*" />

<!-- CLI -->
<PackageReference Include="Spectre.Console" Version="0.55.0" />
<PackageReference Include="Spectre.Console.Cli" Version="0.55.0" />

<!-- Templates -->
<PackageReference Include="Scriban" Version="7.0.6" />

<!-- Updates -->
<PackageReference Include="Velopack" Version="0.0.1298" />
```

---

## Open questions / risks

1. **LiveCharts2 + Avalonia 12**: stable 2.0.0 declares `>= 11.0.0` but has not been explicitly validated against 12.0.0. Test early; fallback to OxyPlot if broken.
2. **Native AOT + plugins**: AOT and `AssemblyLoadContext` for dynamic plugin loading are fundamentally incompatible — AOT trims the assembly at publish time. Decision needed: AOT build OR plugin system, not both.
3. **SIGTERM on Windows**: there is no clean equivalent; Apache/MySQL on Windows must be stopped via their own CLI (`httpd -k stop`, `mysqladmin shutdown`).
4. **TrayIcon on Wayland Linux**: Wayland does not have a system tray spec; StatusNotifierItem support depends on the desktop shell. Test on target Linux distributions.
