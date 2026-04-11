# NKS WebDev Console — Plugin SDK Reference

This document describes how to write a third-party plugin for NKS WDC. A plugin is
a single .NET 9 class library DLL that the daemon discovers at startup, loads into
its own `AssemblyLoadContext`, and registers with the host's DI container and REST
router.

> **Audience:** developers extending NKS WDC with new managed services
> (e.g. Postgres, Nginx, Vite dev server) or non-service tools (e.g. log
> analyzer, schema diff viewer). For configuring an installed plugin, see
> the user guide instead.

---

## 1. Project Layout

A minimal plugin is one csproj plus one C# file:

```
NKS.WebDevConsole.Plugin.HelloWorld/
├── HelloWorldPlugin.cs
├── plugin.json
├── Resources/
│   └── icon.svg                       # optional, surfaced via /api/plugins/{id}/icon
└── NKS.WebDevConsole.Plugin.HelloWorld.csproj
```

The csproj must reference the SDK and enable dynamic loading:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>NKS.WebDevConsole.Plugin.HelloWorld</AssemblyName>
    <RootNamespace>NKS.WebDevConsole.Plugin.HelloWorld</RootNamespace>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.5" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Resources\icon.svg" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference
      Include="..\..\daemon\NKS.WebDevConsole.Plugin.SDK\NKS.WebDevConsole.Plugin.SDK.csproj" />
  </ItemGroup>
</Project>
```

`EnableDynamicLoading=true` is what tells the host to honor the plugin's own
copy of dependencies if any clash with the daemon's. Without it shared types
would resolve to the daemon's version even if the plugin shipped a newer one.

---

## 2. The `plugin.json` Manifest

Located next to the DLL. The daemon reads it for metadata and capabilities:

```json
{
  "id": "nks.wdc.helloworld",
  "displayName": "Hello World",
  "version": "1.0.0",
  "description": "A minimal NKS WDC plugin example",
  "author": "Your Name",
  "license": "Apache-2.0",
  "entryAssembly": "NKS.WebDevConsole.Plugin.HelloWorld.dll",
  "entryType": "NKS.WebDevConsole.Plugin.HelloWorld.HelloWorldPlugin",
  "serviceId": "helloworld",
  "serviceType": "Tool",
  "defaultPorts": [],
  "supportedPlatforms": ["windows", "macos", "linux"],
  "minDaemonVersion": "1.0.0",
  "dependencies": [],
  "capabilities": ["start", "stop"]
}
```

`id` must match the C# `IWdcPlugin.Id` returned at runtime. `serviceType`
is one of `WebServer`, `Database`, `Cache`, `MailServer`, `Tool`.

---

## 3. The Plugin Entry Point

Implement `IWdcPlugin` (or extend `PluginBase` for a smaller surface):

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.HelloWorld;

public sealed class HelloWorldPlugin : PluginBase
{
    public override string Id => "nks.wdc.helloworld";
    public override string DisplayName => "Hello World";
    public override string Version => "1.0.0";

    public override void Initialize(IServiceCollection services, IPluginContext context)
    {
        // Register your own services into the DI container before the daemon builds it.
        services.AddSingleton<HelloWorldGreeter>();
    }

    public override async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<HelloWorldPlugin>();
        var greeter = context.ServiceProvider.GetRequiredService<HelloWorldGreeter>();
        logger.LogInformation("Hello plugin starting: {Greeting}", greeter.Greeting());
    }
}

public sealed class HelloWorldGreeter
{
    public string Greeting() => "hello from a plugin";
}
```

The lifecycle:

1. `Initialize(services, context)` runs **before** the host builds the DI container.
   This is your only chance to add singletons, hosted services, options, etc.
2. `StartAsync(context, ct)` runs **after** the container is built. The
   `context.ServiceProvider` is fully populated. Spawn workers, open files, etc.
3. `StopAsync(ct)` runs at daemon shutdown. Release resources gracefully.

`PluginBase` provides no-op defaults so you only override what you need.

---

## 4. Implementing a Service Plugin

If your plugin manages a long-running process (like Apache or PostgreSQL),
register an `IServiceModule` so the daemon's `ProcessManager` and `HealthMonitor`
can supervise it:

```csharp
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

public sealed class PostgresModule : IServiceModule
{
    public string ServiceId    => "postgres";
    public string DisplayName  => "PostgreSQL";
    public ServiceType Type    => ServiceType.Database;

    public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct) { /* ... */ }
    public Task StartAsync(CancellationToken ct)                            { /* ... */ }
    public Task StopAsync(CancellationToken ct)                             { /* ... */ }
    public Task ReloadAsync(CancellationToken ct)                           { /* ... */ }
    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)         { /* ... */ }
    public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct) { /* ... */ }
}
```

Register both the plugin and the module in `Initialize`:

```csharp
public override void Initialize(IServiceCollection services, IPluginContext context)
{
    services.AddSingleton<PostgresModule>();
    services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<PostgresModule>());
}
```

That second line is the important one — registering the singleton **as**
`IServiceModule` is how `HealthMonitor`, `/api/services`, and the sidebar pick
it up. Forget it and your plugin loads but stays invisible.

### Process spawning

Whenever you start a child process, hand its handle to the daemon's Job Object
so it gets killed if the daemon crashes:

```csharp
var process = new System.Diagnostics.Process { StartInfo = psi };
process.Start();
NKS.WebDevConsole.Core.Services.DaemonJobObject.AssignProcess(process);
```

Without this, an abnormal daemon exit leaves orphaned services holding ports
and the next start fails with "port already in use".

### Metrics

Use the shared sampler so all services report CPU% the same way:

```csharp
public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
{
    var (cpu, memory) = NKS.WebDevConsole.Core.Services
        .ProcessMetricsSampler.Sample(_process);
    var uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;
    return Task.FromResult(new ServiceStatus(
        ServiceId, DisplayName, _state, _process?.Id, cpu, memory, uptime));
}
```

`ProcessMetricsSampler` keeps a per-PID snapshot so the second sample reports
delta CPU instead of cumulative average since process start.

---

## 5. UI Schema (Optional)

If you want a custom panel in the sidebar, implement `IFrontendPanelProvider`
and use `UiSchemaBuilder`:

```csharp
public PluginUiDefinition GetUiDefinition() =>
    new UiSchemaBuilder(Id)
        .Category("Databases")
        .Icon("el-icon-coin")
        .AddServiceCard("postgres")
        .AddConfigEditor("postgres")
        .AddLogViewer("postgres")
        .AddMetricsChart("postgres")
        .Build();
```

Available built-in panel types:

- `service-status-card` — shows running/stopped state, CPU/RAM, start/stop button
- `version-switcher` — version grid for plugins managing multiple installs
- `config-editor` — Monaco editor with syntax highlighting and validation
- `log-viewer` — xterm.js log stream
- `metrics-chart` — ECharts CPU/RAM sparkline

Custom panels: ship a Vue bundle and reference it via `bundleUrl` in the
`PluginUiDefinition` constructor — see `wdc-poc/src/plugins/PluginRegistry.ts`
for the loader contract.

---

## 6. Brand Icon

Drop a square SVG into `Resources/icon.svg` and add it as `EmbeddedResource` in
the csproj. The daemon serves it at:

```
GET /api/plugins/{shortId}/icon
```

The frontend's `ServiceIcon` component fetches this URL automatically based on
the plugin's `serviceId`. No frontend changes needed — your plugin shows up in
the sidebar with the correct logo on first load.

---

## 7. Logging

`IPluginContext.GetLogger<T>()` returns a standard `Microsoft.Extensions.Logging`
ILogger backed by the daemon's logger factory. Use it for everything; do not
write to `Console.Out` directly because the daemon may run as a Windows service
where stdout is silently discarded.

---

## 8. Storage

Persistent plugin state should live under
`%USERPROFILE%/.wdc/plugins/{id}/` (Windows) or `~/.wdc/plugins/{id}/` (Unix).
The daemon creates `~/.wdc/` for you but **not** the per-plugin subdirectory —
do that yourself in `StartAsync`. Examples already follow this pattern: SSL
plugin keeps certs under `~/.wdc/ssl/sites/{domain}/`, Caddy puts its config
under `~/.wdc/caddy/`.

For SQLite-based plugins use the same approach as the daemon's `state.db`:
`Microsoft.Data.Sqlite` with WAL mode and DPAPI-encrypted secrets via
`NKS.WebDevConsole.Core.Services.MySqlRootPassword` as a reference pattern.

---

## 9. Distribution

Build with `dotnet build -c Release` and ship the `bin/Release/net9.0/` output
as a zip. Users drop the zip contents into:

```
%LOCALAPPDATA%/NKS WebDev Console/plugins/NKS.WebDevConsole.Plugin.HelloWorld/
```

The daemon scans `plugins/` on startup. To hot-reload after dropping a new
plugin without restarting the user just clicks Refresh in the Plugin Manager
UI — `PluginLoader` rescans the directory.

For wider distribution submit a PR to the marketplace manifest at
`https://wdc.nks-hub.cz/marketplace/plugins.json` (default fetch target). Once
merged, users see your plugin in the **Marketplace** tab of the Plugin Manager
and can install it with one click.

---

## 10. Testing Your Plugin

The `tests/NKS.WebDevConsole.Daemon.Tests` project shows the integration
pattern: spin up an in-process WebApplicationFactory, point `pluginDir` at a
temp folder containing your built DLL, hit `/api/plugins` and verify it
appears. For unit testing your `IServiceModule` use `Moq` against
`IPluginContext` and assert on the produced `ServiceStatus`.

---

## 11. Cross-ALC Pitfalls

Plugins load in a separate `AssemblyLoadContext` for dependency isolation. The
`SharedAssemblies` set in `PluginLoadContext` covers Core, SDK, DI Abstractions,
and Logging Abstractions. Anything else is loaded fresh from your plugin's
folder. Two consequences:

1. **Type identity:** if you reference a type from a non-shared assembly the
   daemon also uses, the cast will fail across the ALC boundary. Example: do
   not depend on `Newtonsoft.Json` if the daemon ships with a different version.
2. **Reflection bridges:** the daemon calls plugin methods via reflection
   (`MethodInfo.Invoke`) when crossing the ALC, see `SiteOrchestrator.cs` for
   examples. If you expose an API the daemon needs to call, keep the method
   signature in terms of primitive types or types declared in the SDK.

---

## 12. Where to Look in the Source Tree

| File | What it teaches |
|------|----------------|
| `src/plugins/NKS.WebDevConsole.Plugin.Apache/ApachePlugin.cs` | Plugin entry + DI registration |
| `src/plugins/NKS.WebDevConsole.Plugin.Apache/ApacheModule.cs` | Long-running service module |
| `src/plugins/NKS.WebDevConsole.Plugin.PHP/PhpModule.cs` | Multi-version manager + multiple processes |
| `src/plugins/NKS.WebDevConsole.Plugin.Mailpit/MailpitModule.cs` | Minimal IServiceModule (good copy-paste base) |
| `src/plugins/NKS.WebDevConsole.Plugin.Caddy/CaddyModule.cs` | Plugin with admin API health check + reload |
| `src/plugins/NKS.WebDevConsole.Plugin.SSL/SslPlugin.cs` | Tool plugin (not a service) wrapping a CLI |
| `src/daemon/NKS.WebDevConsole.Plugin.SDK/UiSchemaBuilder.cs` | All built-in panel types |

Start by copying `Plugin.Mailpit/` — it is the smallest fully-functional
service plugin in the tree, weighs about 350 lines, and exercises every
SDK extension point.
