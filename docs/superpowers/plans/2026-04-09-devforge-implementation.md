# DevForge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build DevForge -- a cross-platform local dev server manager (MAMP PRO replacement) using C#/.NET 9 + Avalonia UI 12.x

**Architecture:** .NET Worker Service daemon manages Apache/MySQL/PHP-FPM processes via gRPC IPC. Avalonia GUI + System.CommandLine CLI are thin clients. Config pipeline: TOML -> Scriban -> httpd -t -> atomic write. Plugin system via AssemblyLoadContext.

**Tech Stack:** C# .NET 9, Avalonia 12.x, gRPC (Grpc.AspNetCore), SQLite (Microsoft.Data.Sqlite + Dapper), Scriban, CliWrap, LiveCharts2, CommunityToolkit.Mvvm, System.CommandLine, Spectre.Console

---

## Data Ownership (SPEC.md Section 8)

**TOML files** = source of truth for **site configuration**:
- `~/.devforge/sites/{domain}.toml` -- human-editable, diffable, git-friendly
- Daemon reads TOML -> renders Scriban templates -> generates Apache/Nginx configs

**SQLite database** = source of truth for **runtime state and relationships**:
- Service PIDs, health status, restart counts
- PHP version install paths, extension states
- Certificate IDs, expiry dates, fingerprints
- Config change audit trail

**Sync direction:** TOML -> SQLite. On daemon start, all TOML files are scanned and SQLite `sites` table is reconciled. SQLite is NEVER written back to TOML -- it is a derived/cached view. If TOML and SQLite disagree, TOML wins.

---

## Phase 0: Day-1 Verification

**Purpose:** Verify that ALL third-party dependencies actually work together before writing application code. Any failure here updates SPEC.md with workarounds. (SPEC.md Section 22, Phase 0)

---

### Task 0.1: Install Avalonia Templates

**Files:**
- (none -- environment setup only)

**Steps:**
- [ ] Run `dotnet new install Avalonia.Templates`
- [ ] Verify templates are listed: `dotnet new list | grep -i avalonia`
- [ ] Confirm `avalonia.app` and `avalonia.mvvm` templates exist

**Run:**
```bash
dotnet new install Avalonia.Templates
dotnet new list | grep -i avalonia
```

**Commit:**
```bash
# No commit -- environment setup only, nothing to track
```

---

### Task 0.2: Verify Avalonia 12 FluentTheme Dark Mode

**Files:**
- `src/DevForge.Verify/DevForge.Verify.csproj` (create)
- `src/DevForge.Verify/Program.cs` (create)
- `src/DevForge.Verify/MainWindow.axaml` (create)
- `src/DevForge.Verify/MainWindow.axaml.cs` (create)

**Steps:**
- [ ] Create new Avalonia app: `dotnet new avalonia.app -o src/DevForge.Verify --framework net9.0`
- [ ] Open `DevForge.Verify.csproj`, confirm Avalonia 12.x package references
- [ ] Edit `App.axaml` to set `RequestedThemeVariant="Dark"` and include `<FluentTheme />`
- [ ] Edit `MainWindow.axaml` to add a TextBlock "DevForge Verification" with dark background
- [ ] Run `dotnet run --project src/DevForge.Verify` -- verify dark window appears
- [ ] If FluentTheme fails, check Avalonia 12 API changes and fix

**Code (`App.axaml`):**
```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="DevForge.Verify.App"
             RequestedThemeVariant="Dark">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
</Application>
```

**Code (`MainWindow.axaml`):**
```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="DevForge.Verify.MainWindow"
        Title="DevForge - Verify" Width="600" Height="400">
  <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
    <TextBlock Text="DevForge Verification" FontSize="24" FontWeight="Bold" />
    <TextBlock Text="FluentTheme Dark Mode: OK" FontSize="14" Margin="0,8,0,0" />
  </StackPanel>
</Window>
```

**Test:** Visual verification -- dark window renders with white text on dark background.

**Run:**
```bash
dotnet run --project src/DevForge.Verify
```

**Commit:**
```bash
git add src/DevForge.Verify/
git commit -m "chore: verify avalonia 12 dark mode renders"
```

---

### Task 0.3: Verify LiveCharts2 on Avalonia 12

**Files:**
- `src/DevForge.Verify/DevForge.Verify.csproj` (modify)
- `src/DevForge.Verify/MainWindow.axaml` (modify)

**Steps:**
- [ ] Add package: `dotnet add src/DevForge.Verify package LiveChartsCore.SkiaSharpView.Avalonia`
- [ ] Edit `MainWindow.axaml` to add a `CartesianChart` with sample data
- [ ] Run and confirm chart renders without exceptions
- [ ] If LiveCharts2 throws on Avalonia 12: try `dotnet add package ScottPlot.Avalonia` as fallback
- [ ] Document result: update SPEC.md if fallback is needed

**Code (`MainWindow.axaml` addition):**
```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:lvc="using:LiveChartsCore.SkiaSharpView.Avalonia"
        x:Class="DevForge.Verify.MainWindow"
        Title="DevForge - Verify" Width="800" Height="500">
  <StackPanel Spacing="16" Margin="16">
    <TextBlock Text="LiveCharts2 Verification" FontSize="18" FontWeight="Bold" />
    <lvc:CartesianChart Height="300" x:Name="Chart" />
  </StackPanel>
</Window>
```

**Code (`MainWindow.axaml.cs`):**
```csharp
using Avalonia.Controls;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace DevForge.Verify;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Chart.Series = new ISeries[]
        {
            new LineSeries<double> { Values = [3, 7, 2, 9, 4, 6, 8, 1, 5] }
        };
    }
}
```

**Test:** Visual verification -- chart renders a line series without crash or blank area.

**Run:**
```bash
dotnet run --project src/DevForge.Verify
```

**Commit:**
```bash
git commit -am "chore: verify livecharts2 renders in avalonia 12"
```

---

### Task 0.4: Verify gRPC + Kestrel Named Pipe on Windows

**Files:**
- `src/DevForge.Verify.Grpc/DevForge.Verify.Grpc.csproj` (create)
- `src/DevForge.Verify.Grpc/Program.cs` (create)
- `src/DevForge.Verify.Grpc/Protos/greeter.proto` (create)

**Steps:**
- [ ] Create project: `dotnet new web -o src/DevForge.Verify.Grpc --framework net9.0`
- [ ] Add packages: `Grpc.AspNetCore`, `Grpc.Net.Client`
- [ ] Create `greeter.proto` with a simple `SayHello` RPC
- [ ] Configure Kestrel to listen on named pipe `devforge-verify`
- [ ] Implement server and inline client test
- [ ] Run and confirm gRPC call succeeds over named pipe
- [ ] Verify on Windows (named pipe) -- macOS/Linux uses Unix socket

**Code (`greeter.proto`):**
```protobuf
syntax = "proto3";
package verify;
service Greeter {
  rpc SayHello (HelloRequest) returns (HelloReply);
}
message HelloRequest { string name = 1; }
message HelloReply { string message = 1; }
```

**Code (`Program.cs`):**
```csharp
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Grpc.Net.Client;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenNamedPipe("devforge-verify", listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});
builder.Services.AddGrpc();
var app = builder.Build();
app.MapGrpcService<GreeterService>();

// Start server in background, then run client test
_ = Task.Run(async () =>
{
    await Task.Delay(2000);
    var handler = new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var clientStream = new System.IO.Pipes.NamedPipeClientStream(
                ".", "devforge-verify", System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.WriteThrough | System.IO.Pipes.PipeOptions.Asynchronous);
            await clientStream.ConnectAsync(ct);
            return clientStream;
        }
    };
    var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = handler
    });
    var client = new Verify.Greeter.GreeterClient(channel);
    var reply = await client.SayHelloAsync(new Verify.HelloRequest { Name = "DevForge" });
    Console.WriteLine($"gRPC Response: {reply.Message}");
    Console.WriteLine("VERIFICATION PASSED: gRPC over named pipe works");
    Environment.Exit(0);
});

app.Run();

public class GreeterService : Verify.Greeter.GreeterBase
{
    public override Task<Verify.HelloReply> SayHello(Verify.HelloRequest request,
        Grpc.Core.ServerCallContext context)
    {
        return Task.FromResult(new Verify.HelloReply { Message = $"Hello {request.Name}" });
    }
}
```

**Test:** Console output shows "VERIFICATION PASSED: gRPC over named pipe works"

**Run:**
```bash
dotnet run --project src/DevForge.Verify.Grpc
```

**Commit:**
```bash
git add src/DevForge.Verify.Grpc/
git commit -m "chore: verify grpc named pipe transport on windows"
```

---

### Task 0.5: Verify dotnet publish + Windows Defender Scan

**Files:**
- (none -- build verification)

**Steps:**
- [ ] Publish: `dotnet publish src/DevForge.Verify --runtime win-x64 --self-contained true -o publish/verify`
- [ ] Do NOT add `/p:PublishTrimmed=true` (triggers Defender heuristics per SPEC.md Section 2)
- [ ] Open Windows Security > Virus & Threat Protection > Scan options > Custom scan
- [ ] Scan the `publish/verify/` directory
- [ ] Confirm zero detections
- [ ] If flagged: check if `PublishReadyToRun=true` helps, update SPEC.md

**Run:**
```bash
dotnet publish src/DevForge.Verify --runtime win-x64 --self-contained true -o publish/verify
# Then manually scan publish/verify/ with Windows Defender
```

**Commit:**
```bash
# No commit -- verification only. Delete src/DevForge.Verify* after Phase 0 passes.
```

---

## Phase 1: Foundation

**Purpose:** Create the solution structure, core types, gRPC protocol, database schema, and minimal working daemon + GUI + CLI that connect via gRPC. (SPEC.md Section 22, Phase 1)

---

### Task 1.1: Create Solution and Project Structure

**Files:**
- `DevForge.sln` (create)
- `src/DevForge.Core/DevForge.Core.csproj` (create)
- `src/DevForge.Daemon/DevForge.Daemon.csproj` (create)
- `src/DevForge.Gui/DevForge.Gui.csproj` (create)
- `src/DevForge.Cli/DevForge.Cli.csproj` (create)
- `tests/DevForge.Core.Tests/DevForge.Core.Tests.csproj` (create)
- `tests/DevForge.Daemon.Tests/DevForge.Daemon.Tests.csproj` (create)

**Steps:**
- [ ] Create solution: `dotnet new sln -n DevForge`
- [ ] Create Core class library: `dotnet new classlib -o src/DevForge.Core --framework net9.0`
- [ ] Create Daemon worker service: `dotnet new worker -o src/DevForge.Daemon --framework net9.0`
- [ ] Create GUI Avalonia app: `dotnet new avalonia.app -o src/DevForge.Gui --framework net9.0`
- [ ] Create CLI console app: `dotnet new console -o src/DevForge.Cli --framework net9.0`
- [ ] Create test projects: `dotnet new xunit -o tests/DevForge.Core.Tests` and `dotnet new xunit -o tests/DevForge.Daemon.Tests`
- [ ] Add all projects to solution: `dotnet sln add src/DevForge.Core src/DevForge.Daemon src/DevForge.Gui src/DevForge.Cli tests/DevForge.Core.Tests tests/DevForge.Daemon.Tests`
- [ ] Add project references per SPEC.md Section 3 dependency graph
- [ ] Add NuGet packages to each project (see code block below)
- [ ] Run `dotnet build` and confirm zero errors
- [ ] Delete template boilerplate files (Worker.cs from Daemon, Class1.cs from Core)

**Code (project references):**
```bash
# Core has no DevForge dependencies
# Daemon -> Core
dotnet add src/DevForge.Daemon reference src/DevForge.Core
# Gui -> Core
dotnet add src/DevForge.Gui reference src/DevForge.Core
# Cli -> Core
dotnet add src/DevForge.Cli reference src/DevForge.Core
# Tests -> all
dotnet add tests/DevForge.Core.Tests reference src/DevForge.Core
dotnet add tests/DevForge.Daemon.Tests reference src/DevForge.Core src/DevForge.Daemon
```

**Code (NuGet packages):**
```bash
# Core
dotnet add src/DevForge.Core package Google.Protobuf
dotnet add src/DevForge.Core package Grpc.Tools
dotnet add src/DevForge.Core package Tomlyn

# Daemon
dotnet add src/DevForge.Daemon package Grpc.AspNetCore
dotnet add src/DevForge.Daemon package Microsoft.Data.Sqlite
dotnet add src/DevForge.Daemon package Dapper
dotnet add src/DevForge.Daemon package Scriban
dotnet add src/DevForge.Daemon package CliWrap
dotnet add src/DevForge.Daemon package Serilog
dotnet add src/DevForge.Daemon package Serilog.Sinks.File
dotnet add src/DevForge.Daemon package Serilog.Extensions.Hosting
dotnet add src/DevForge.Daemon package DbUp.SQLite

# Gui
dotnet add src/DevForge.Gui package Avalonia
dotnet add src/DevForge.Gui package Avalonia.Desktop
dotnet add src/DevForge.Gui package Avalonia.Themes.Fluent
dotnet add src/DevForge.Gui package LiveChartsCore.SkiaSharpView.Avalonia
dotnet add src/DevForge.Gui package Grpc.Net.Client
dotnet add src/DevForge.Gui package CommunityToolkit.Mvvm

# Cli
dotnet add src/DevForge.Cli package System.CommandLine
dotnet add src/DevForge.Cli package Grpc.Net.Client
dotnet add src/DevForge.Cli package Spectre.Console

# Tests
dotnet add tests/DevForge.Core.Tests package Moq
dotnet add tests/DevForge.Daemon.Tests package Moq
dotnet add tests/DevForge.Daemon.Tests package Microsoft.Data.Sqlite
```

**Test:**
```csharp
// tests/DevForge.Core.Tests/SolutionStructureTests.cs
namespace DevForge.Core.Tests;

public class SolutionStructureTests
{
    [Fact]
    public void Core_Assembly_Loads()
    {
        var assembly = typeof(DevForge.Core.Models.ServiceType).Assembly;
        Assert.NotNull(assembly);
        Assert.Contains("DevForge.Core", assembly.FullName);
    }
}
```

**Run:**
```bash
dotnet build DevForge.sln
dotnet test tests/DevForge.Core.Tests
```

**Commit:**
```bash
git add -A
git commit -m "feat: create solution with 5 projects and test infrastructure"
```

---

### Task 1.2: Define Core Enums and Models

**Files:**
- `src/DevForge.Core/Models/ServiceType.cs` (create)
- `src/DevForge.Core/Models/ServiceState.cs` (create)
- `src/DevForge.Core/Models/ServiceStatus.cs` (create)
- `src/DevForge.Core/Models/ValidationResult.cs` (create)
- `src/DevForge.Core/Models/Framework.cs` (create)
- `src/DevForge.Core/Models/RestartPolicy.cs` (create)
- `tests/DevForge.Core.Tests/Models/ServiceStateTests.cs` (create)

**Steps:**
- [ ] Create `src/DevForge.Core/Models/` directory
- [ ] Define `ServiceType` enum (SPEC.md Section 5.3)
- [ ] Define `ServiceState` enum (SPEC.md Section 5.1)
- [ ] Define `ServiceStatus` record
- [ ] Define `ValidationResult` record
- [ ] Define `Framework` enum for auto-detection
- [ ] Define `RestartPolicy` class
- [ ] Write tests for enum values and record equality
- [ ] Run tests

**Code:**
```csharp
// src/DevForge.Core/Models/ServiceType.cs
namespace DevForge.Core.Models;

public enum ServiceType
{
    WebServer,
    Database,
    PhpRuntime,
    Cache,
    Mail,
    Proxy,
    Custom
}
```

```csharp
// src/DevForge.Core/Models/ServiceState.cs
namespace DevForge.Core.Models;

public enum ServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
    Restarting,
    Disabled
}
```

```csharp
// src/DevForge.Core/Models/ServiceStatus.cs
namespace DevForge.Core.Models;

public record ServiceStatus(
    ServiceState State,
    int? Pid,
    TimeSpan Uptime,
    int RestartCount,
    double CpuPercent,
    long MemoryBytes);
```

```csharp
// src/DevForge.Core/Models/ValidationResult.cs
namespace DevForge.Core.Models;

public record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success() => new(true, Array.Empty<string>());
    public static ValidationResult Failure(params string[] errors) => new(false, errors);
}
```

```csharp
// src/DevForge.Core/Models/Framework.cs
namespace DevForge.Core.Models;

public enum Framework
{
    Generic,
    Nette,
    Laravel,
    WordPress,
    Symfony
}
```

```csharp
// src/DevForge.Core/Models/RestartPolicy.cs
namespace DevForge.Core.Models;

public class RestartPolicy
{
    public int MaxRestarts { get; set; } = 5;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan GetBackoff(int attempt)
    {
        var seconds = Math.Min(
            BackoffBase.TotalSeconds * Math.Pow(2, attempt),
            BackoffMax.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
```

**Test:**
```csharp
// tests/DevForge.Core.Tests/Models/ServiceStateTests.cs
namespace DevForge.Core.Tests.Models;

using DevForge.Core.Models;

public class ServiceStateTests
{
    [Fact]
    public void ServiceState_Has_All_Required_Values()
    {
        var values = Enum.GetValues<ServiceState>();
        Assert.Contains(ServiceState.Stopped, values);
        Assert.Contains(ServiceState.Starting, values);
        Assert.Contains(ServiceState.Running, values);
        Assert.Contains(ServiceState.Stopping, values);
        Assert.Contains(ServiceState.Crashed, values);
        Assert.Contains(ServiceState.Disabled, values);
    }

    [Fact]
    public void ValidationResult_Success_Is_Valid()
    {
        var result = ValidationResult.Success();
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidationResult_Failure_Has_Errors()
    {
        var result = ValidationResult.Failure("bad config", "missing field");
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void RestartPolicy_Backoff_Is_Exponential()
    {
        var policy = new RestartPolicy();
        Assert.Equal(2, policy.GetBackoff(0).TotalSeconds);
        Assert.Equal(4, policy.GetBackoff(1).TotalSeconds);
        Assert.Equal(8, policy.GetBackoff(2).TotalSeconds);
    }

    [Fact]
    public void RestartPolicy_Backoff_Caps_At_Max()
    {
        var policy = new RestartPolicy { BackoffMax = TimeSpan.FromSeconds(10) };
        Assert.Equal(10, policy.GetBackoff(10).TotalSeconds);
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Core.Tests
```

**Commit:**
```bash
git add -A
git commit -m "feat: define core enums and models"
```

---

### Task 1.3: Define IServiceModule Interface

**Files:**
- `src/DevForge.Core/Interfaces/IServiceModule.cs` (create)
- `src/DevForge.Core/Models/CliCommandDefinition.cs` (create)
- `tests/DevForge.Core.Tests/Interfaces/IServiceModuleTests.cs` (create)

**Steps:**
- [ ] Create `src/DevForge.Core/Interfaces/` directory
- [ ] Define `IServiceModule` interface per SPEC.md Section 17
- [ ] Define `CliCommandDefinition` record
- [ ] Write test with a mock implementation to verify interface compiles
- [ ] Run tests

**Code:**
```csharp
// src/DevForge.Core/Interfaces/IServiceModule.cs
namespace DevForge.Core.Interfaces;

using DevForge.Core.Models;

public interface IServiceModule
{
    string ServiceId { get; }
    string DisplayName { get; }
    ServiceType Type { get; }
    string DefaultConfigTemplate { get; }

    Task<ValidationResult> ValidateConfigAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task ReloadAsync(CancellationToken ct);
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct);

    Type? DashboardPanelType => null;
    IReadOnlyList<CliCommandDefinition> CliCommands => Array.Empty<CliCommandDefinition>();
}
```

```csharp
// src/DevForge.Core/Models/CliCommandDefinition.cs
namespace DevForge.Core.Models;

public record CliCommandDefinition(
    string Name,
    string Description,
    Func<string[], Task<int>> Handler);
```

**Test:**
```csharp
// tests/DevForge.Core.Tests/Interfaces/IServiceModuleTests.cs
namespace DevForge.Core.Tests.Interfaces;

using DevForge.Core.Interfaces;
using DevForge.Core.Models;
using Moq;

public class IServiceModuleTests
{
    [Fact]
    public async Task Mock_ServiceModule_Returns_Status()
    {
        var mock = new Mock<IServiceModule>();
        mock.Setup(m => m.ServiceId).Returns("test-service");
        mock.Setup(m => m.DisplayName).Returns("Test Service");
        mock.Setup(m => m.Type).Returns(ServiceType.Custom);
        mock.Setup(m => m.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceStatus(ServiceState.Stopped, null, TimeSpan.Zero, 0, 0, 0));

        var status = await mock.Object.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ServiceState.Stopped, status.State);
        Assert.Equal("test-service", mock.Object.ServiceId);
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Core.Tests
```

**Commit:**
```bash
git add -A
git commit -m "feat: define IServiceModule interface and CliCommandDefinition"
```

---

### Task 1.4: Write devforge.proto gRPC Service Definition

**Files:**
- `src/DevForge.Core/Proto/devforge.proto` (create)
- `src/DevForge.Core/DevForge.Core.csproj` (modify -- add Protobuf item)
- `src/DevForge.Daemon/DevForge.Daemon.csproj` (modify -- reference proto)
- `src/DevForge.Gui/DevForge.Gui.csproj` (modify -- reference proto)
- `src/DevForge.Cli/DevForge.Cli.csproj` (modify -- reference proto)

**Steps:**
- [ ] Create `src/DevForge.Core/Proto/` directory
- [ ] Write complete `devforge.proto` with all 30 RPC methods from SPEC.md Section 7
- [ ] Configure `DevForge.Core.csproj` with `<Protobuf Include="Proto/devforge.proto" GrpcServices="None" />`
- [ ] Configure `DevForge.Daemon.csproj` with `<Protobuf Include="../DevForge.Core/Proto/devforge.proto" GrpcServices="Server" Link="Proto/devforge.proto" />`
- [ ] Configure client projects with `GrpcServices="Client"`
- [ ] Run `dotnet build` to verify proto compilation succeeds
- [ ] Verify generated C# classes exist in obj/ directory

**Code (`devforge.proto`):**
```protobuf
syntax = "proto3";
package devforge.v1;

import "google/protobuf/empty.proto";
import "google/protobuf/timestamp.proto";

option csharp_namespace = "DevForge.Proto";

service DevForgeService {
  rpc GetStatus(google.protobuf.Empty) returns (DaemonStatus);
  rpc StartService(ServiceRequest) returns (ServiceResponse);
  rpc StopService(ServiceRequest) returns (ServiceResponse);
  rpc RestartService(ServiceRequest) returns (ServiceResponse);
  rpc GetServiceStatus(ServiceRequest) returns (ServiceStatusResponse);
  rpc ListServices(google.protobuf.Empty) returns (ServiceListResponse);
  rpc CreateSite(CreateSiteRequest) returns (SiteResponse);
  rpc UpdateSite(UpdateSiteRequest) returns (SiteResponse);
  rpc DeleteSite(DeleteSiteRequest) returns (google.protobuf.Empty);
  rpc GetSite(SiteRequest) returns (SiteResponse);
  rpc ListSites(google.protobuf.Empty) returns (SiteListResponse);
  rpc ListPhpVersions(google.protobuf.Empty) returns (PhpVersionListResponse);
  rpc SetDefaultPhpVersion(SetDefaultPhpRequest) returns (PhpVersionResponse);
  rpc ListDatabases(google.protobuf.Empty) returns (DatabaseListResponse);
  rpc CreateDatabase(CreateDatabaseRequest) returns (DatabaseResponse);
  rpc DropDatabase(DropDatabaseRequest) returns (google.protobuf.Empty);
  rpc GenerateCert(GenerateCertRequest) returns (CertResponse);
  rpc InstallCa(google.protobuf.Empty) returns (CaInstallResponse);
  rpc ListCerts(google.protobuf.Empty) returns (CertListResponse);
  rpc GetHostsStatus(google.protobuf.Empty) returns (HostsStatusResponse);
  rpc FlushDns(google.protobuf.Empty) returns (google.protobuf.Empty);
  rpc StreamLogs(LogRequest) returns (stream LogEntry);
  rpc StreamMetrics(MetricsRequest) returns (stream ServiceMetrics);
  rpc ListPlugins(google.protobuf.Empty) returns (PluginListResponse);
}

message DaemonStatus {
  string version = 1;
  bool running = 2;
  int32 uptime_seconds = 3;
  repeated ServiceStatusResponse services = 4;
}

message ServiceRequest { string service_id = 1; }
message ServiceResponse { bool success = 1; string message = 2; }
message ServiceStatusResponse {
  string service_id = 1;
  string display_name = 2;
  string state = 3;
  int32 pid = 4;
  int64 uptime_seconds = 5;
  float cpu_percent = 6;
  int64 memory_bytes = 7;
  int32 restart_count = 8;
}
message ServiceListResponse { repeated ServiceStatusResponse services = 1; }

message CreateSiteRequest {
  string domain = 1;
  string document_root = 2;
  string php_version = 3;
  string webserver = 4;
  bool ssl_enabled = 5;
  bool create_database = 6;
  string database_name = 7;
  string framework = 8;
  repeated string aliases = 9;
  string custom_directives = 10;
}
message UpdateSiteRequest {
  string domain = 1;
  string document_root = 2;
  string php_version = 3;
  bool ssl_enabled = 4;
  repeated string aliases = 5;
}
message DeleteSiteRequest { string domain = 1; bool confirm = 2; }
message SiteRequest { string domain = 1; }
message SiteResponse {
  int64 id = 1;
  string domain = 2;
  string document_root = 3;
  string php_version = 4;
  string webserver = 5;
  bool ssl_enabled = 6;
  string status = 7;
  repeated string aliases = 8;
  string config_path = 9;
  google.protobuf.Timestamp created_at = 10;
}
message SiteListResponse { repeated SiteResponse sites = 1; }

message SetDefaultPhpRequest { string version = 1; }
message PhpVersionResponse { string version = 1; string path = 2; bool is_default = 3; }
message PhpVersionListResponse { repeated PhpVersionResponse versions = 1; }

message CreateDatabaseRequest { string name = 1; string service_id = 2; }
message DropDatabaseRequest { string name = 1; bool confirm = 2; }
message DatabaseResponse { string name = 1; string service_id = 2; int64 size_bytes = 3; }
message DatabaseListResponse { repeated DatabaseResponse databases = 1; }

message GenerateCertRequest { string domain = 1; repeated string sans = 2; }
message CertResponse { string domain = 1; string cert_path = 2; string key_path = 3; string valid_until = 4; }
message CaInstallResponse { bool success = 1; string message = 2; }
message CertListResponse { repeated CertResponse certificates = 1; }

message HostsStatusResponse { repeated HostEntry entries = 1; }
message HostEntry { string ip = 1; string domain = 2; bool managed = 3; }

message LogRequest { string service_id = 1; int32 tail_lines = 2; }
message LogEntry {
  google.protobuf.Timestamp timestamp = 1;
  string service_id = 2;
  string level = 3;
  string message = 4;
}

message MetricsRequest { repeated string service_ids = 1; }
message ServiceMetrics {
  string service_id = 1;
  float cpu_percent = 2;
  int64 memory_bytes = 3;
  int64 uptime_seconds = 4;
  google.protobuf.Timestamp timestamp = 5;
}

message ProgressUpdate { int32 percent = 1; string message = 2; bool done = 3; string error = 4; }
message PluginResponse { string id = 1; string name = 2; string version = 3; bool enabled = 4; }
message PluginListResponse { repeated PluginResponse plugins = 1; }
```

**Test:**
```csharp
// tests/DevForge.Core.Tests/Proto/ProtoCompilationTests.cs
namespace DevForge.Core.Tests.Proto;

using DevForge.Proto;

public class ProtoCompilationTests
{
    [Fact]
    public void DaemonStatus_Can_Be_Created()
    {
        var status = new DaemonStatus
        {
            Version = "1.0.0",
            Running = true,
            UptimeSeconds = 3600
        };
        Assert.Equal("1.0.0", status.Version);
        Assert.True(status.Running);
    }

    [Fact]
    public void CreateSiteRequest_Has_All_Fields()
    {
        var req = new CreateSiteRequest
        {
            Domain = "myapp.loc",
            DocumentRoot = "/var/www/myapp",
            PhpVersion = "8.2",
            Webserver = "apache",
            SslEnabled = true,
            CreateDatabase = true,
            DatabaseName = "myapp",
            Framework = "nette"
        };
        req.Aliases.Add("www.myapp.loc");
        Assert.Equal("myapp.loc", req.Domain);
        Assert.Single(req.Aliases);
    }
}
```

**Run:**
```bash
dotnet build DevForge.sln
dotnet test tests/DevForge.Core.Tests
```

**Commit:**
```bash
git add -A
git commit -m "feat: add complete gRPC proto definition with 24 RPC methods"
```

---

### Task 1.5: Set Up SQLite with Migration Runner

**Files:**
- `src/DevForge.Daemon/Data/DatabaseInitializer.cs` (create)
- `src/DevForge.Daemon/Data/Migrations/001_initial.sql` (copy from prototype)
- `src/DevForge.Daemon/Data/Migrations/002_triggers.sql` (copy from prototype)
- `src/DevForge.Daemon/Data/Migrations/003_views.sql` (copy from prototype)
- `src/DevForge.Daemon/Data/Migrations/004_indexes.sql` (copy from prototype)
- `src/DevForge.Daemon/Data/Migrations/005_seed.sql` (copy from prototype)
- `tests/DevForge.Daemon.Tests/Data/DatabaseInitializerTests.cs` (create)

**Steps:**
- [ ] Create `src/DevForge.Daemon/Data/Migrations/` directory
- [ ] Copy `prototype/database/migrations/001_initial.sql` to `001_initial.sql`
- [ ] Copy `prototype/database/triggers.sql` to `002_triggers.sql`
- [ ] Copy `prototype/database/views.sql` to `003_views.sql`
- [ ] Copy `prototype/database/indexes.sql` to `004_indexes.sql`
- [ ] Copy `prototype/database/seed.sql` to `005_seed.sql`
- [ ] Embed SQL files as EmbeddedResource in csproj
- [ ] Implement `DatabaseInitializer` using DbUp to run migrations in order
- [ ] Write test that applies all migrations to in-memory SQLite
- [ ] Run tests

**Code:**
```csharp
// src/DevForge.Daemon/Data/DatabaseInitializer.cs
using DbUp;
using DbUp.Engine;
using Microsoft.Data.Sqlite;

namespace DevForge.Daemon.Data;

public class DatabaseInitializer
{
    private readonly string _connectionString;

    public DatabaseInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DatabaseUpgradeResult Initialize()
    {
        EnsureDatabase();

        var upgrader = DeployChanges.To
            .SQLiteDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(DatabaseInitializer).Assembly,
                s => s.Contains(".Data.Migrations."))
            .LogToConsole()
            .Build();

        return upgrader.PerformUpgrade();
    }

    public bool IsUpToDate()
    {
        var upgrader = DeployChanges.To
            .SQLiteDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(DatabaseInitializer).Assembly,
                s => s.Contains(".Data.Migrations."))
            .Build();

        return !upgrader.IsUpgradeRequired();
    }

    private void EnsureDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
    }
}
```

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/Data/DatabaseInitializerTests.cs
using DevForge.Daemon.Data;
using Microsoft.Data.Sqlite;
using Dapper;

namespace DevForge.Daemon.Tests.Data;

public class DatabaseInitializerTests
{
    [Fact]
    public void Initialize_Creates_All_Tables()
    {
        var connStr = "Data Source=:memory:";
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        var init = new DatabaseInitializer(connStr);
        var result = init.Initialize();

        Assert.True(result.Successful, string.Join(", ", result.ErrorMessage ?? ""));

        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ToList();

        Assert.Contains("settings", tables);
        Assert.Contains("services", tables);
        Assert.Contains("sites", tables);
        Assert.Contains("php_versions", tables);
        Assert.Contains("certificates", tables);
        Assert.Contains("databases", tables);
        Assert.Contains("plugins", tables);
        Assert.Contains("config_history", tables);
        Assert.Contains("schema_migrations", tables);
    }

    [Fact]
    public void Initialize_Seeds_Default_Services()
    {
        var connStr = "Data Source=:memory:";
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        new DatabaseInitializer(connStr).Initialize();

        var services = conn.Query<string>("SELECT name FROM services").ToList();
        Assert.Contains("apache", services);
        Assert.Contains("mysql", services);
    }

    [Fact]
    public void Initialize_Seeds_Default_Settings()
    {
        var connStr = "Data Source=:memory:";
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        new DatabaseInitializer(connStr).Initialize();

        var httpPort = conn.QuerySingle<string>(
            "SELECT value FROM settings WHERE category='network' AND key='http_port'");
        Assert.Equal("80", httpPort);
    }

    [Fact]
    public void Initialize_Is_Idempotent()
    {
        var connStr = "Data Source=:memory:";
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        var init = new DatabaseInitializer(connStr);
        init.Initialize();
        var result = init.Initialize(); // second run
        Assert.True(result.Successful);
        Assert.True(init.IsUpToDate());
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "DatabaseInitializerTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: sqlite migration runner with prototype schema"
```

---

### Task 1.6: Create SiteConfig TOML Model and Parser

**Files:**
- `src/DevForge.Core/Configuration/SiteConfig.cs` (create)
- `src/DevForge.Core/Configuration/SiteConfigLoader.cs` (create)
- `tests/DevForge.Core.Tests/Configuration/SiteConfigLoaderTests.cs` (create)

**Steps:**
- [ ] Define `SiteConfig` class matching TOML schema from SPEC.md Section 8
- [ ] Implement `SiteConfigLoader` using Tomlyn for reading/writing TOML
- [ ] Write round-trip test: create SiteConfig -> serialize to TOML -> deserialize -> assert equal
- [ ] Write test for Nette framework TOML with php.ini_overrides
- [ ] Run tests

**Code:**
```csharp
// src/DevForge.Core/Configuration/SiteConfig.cs
namespace DevForge.Core.Configuration;

public class SiteConfig
{
    public SiteSection Site { get; set; } = new();
    public PhpSection Php { get; set; } = new();
    public SslSection Ssl { get; set; } = new();
    public ServerSection Server { get; set; } = new();
}

public class SiteSection
{
    public string Hostname { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public string DocumentRoot { get; set; } = "";
    public string Framework { get; set; } = "generic";
}

public class PhpSection
{
    public string Version { get; set; } = "8.3";
    public List<string> Extensions { get; set; } = new();
    public Dictionary<string, string> IniOverrides { get; set; } = new();
}

public class SslSection
{
    public bool Enabled { get; set; }
}

public class ServerSection
{
    public string Type { get; set; } = "apache";
    public string CustomDirectives { get; set; } = "";
}
```

```csharp
// src/DevForge.Core/Configuration/SiteConfigLoader.cs
using Tomlyn;
using Tomlyn.Model;

namespace DevForge.Core.Configuration;

public static class SiteConfigLoader
{
    public static SiteConfig Load(string tomlContent)
    {
        var model = Toml.ToModel(tomlContent);
        var config = new SiteConfig();

        if (model.TryGetValue("site", out var siteObj) && siteObj is TomlTable site)
        {
            config.Site.Hostname = site.GetValueOrDefault<string>("hostname") ?? "";
            config.Site.DocumentRoot = site.GetValueOrDefault<string>("document_root") ?? "";
            config.Site.Framework = site.GetValueOrDefault<string>("framework") ?? "generic";
            if (site.TryGetValue("aliases", out var aliases) && aliases is TomlArray arr)
                config.Site.Aliases = arr.Select(a => a?.ToString() ?? "").ToList();
        }

        if (model.TryGetValue("php", out var phpObj) && phpObj is TomlTable php)
        {
            config.Php.Version = php.GetValueOrDefault<string>("version") ?? "8.3";
            if (php.TryGetValue("extensions", out var ext) && ext is TomlArray extArr)
                config.Php.Extensions = extArr.Select(e => e?.ToString() ?? "").ToList();
            if (php.TryGetValue("ini_overrides", out var ini) && ini is TomlTable iniTable)
                config.Php.IniOverrides = iniTable.ToDictionary(
                    k => k.Key, v => v.Value?.ToString() ?? "");
        }

        if (model.TryGetValue("ssl", out var sslObj) && sslObj is TomlTable ssl)
            config.Ssl.Enabled = ssl.GetValueOrDefault<bool>("enabled");

        if (model.TryGetValue("server", out var srvObj) && srvObj is TomlTable srv)
        {
            config.Server.Type = srv.GetValueOrDefault<string>("type") ?? "apache";
            config.Server.CustomDirectives = srv.GetValueOrDefault<string>("custom_directives") ?? "";
        }

        return config;
    }

    public static string Serialize(SiteConfig config)
    {
        var model = new TomlTable
        {
            ["site"] = new TomlTable
            {
                ["hostname"] = config.Site.Hostname,
                ["document_root"] = config.Site.DocumentRoot,
                ["framework"] = config.Site.Framework,
                ["aliases"] = new TomlArray(config.Site.Aliases.Select(a => (object)a))
            },
            ["php"] = new TomlTable
            {
                ["version"] = config.Php.Version
            },
            ["ssl"] = new TomlTable
            {
                ["enabled"] = config.Ssl.Enabled
            },
            ["server"] = new TomlTable
            {
                ["type"] = config.Server.Type
            }
        };
        return Toml.FromModel(model);
    }

    public static SiteConfig LoadFromFile(string path)
        => Load(File.ReadAllText(path));

    public static void SaveToFile(string path, SiteConfig config)
        => File.WriteAllText(path, Serialize(config));
}

internal static class TomlTableExtensions
{
    public static T? GetValueOrDefault<T>(this TomlTable table, string key)
        => table.TryGetValue(key, out var val) && val is T typed ? typed : default;
}
```

**Test:**
```csharp
// tests/DevForge.Core.Tests/Configuration/SiteConfigLoaderTests.cs
using DevForge.Core.Configuration;

namespace DevForge.Core.Tests.Configuration;

public class SiteConfigLoaderTests
{
    private const string SampleToml = """
        [site]
        hostname = "myapp.loc"
        aliases = ["www.myapp.loc"]
        document_root = "C:\\work\\sites\\myapp\\www"
        framework = "nette"

        [php]
        version = "8.2"
        extensions = ["xdebug", "intl"]

        [php.ini_overrides]
        memory_limit = "512M"
        display_errors = "On"

        [ssl]
        enabled = true

        [server]
        type = "apache"
        """;

    [Fact]
    public void Load_Parses_All_Sections()
    {
        var config = SiteConfigLoader.Load(SampleToml);

        Assert.Equal("myapp.loc", config.Site.Hostname);
        Assert.Equal("nette", config.Site.Framework);
        Assert.Single(config.Site.Aliases);
        Assert.Equal("8.2", config.Php.Version);
        Assert.Contains("xdebug", config.Php.Extensions);
        Assert.True(config.Ssl.Enabled);
        Assert.Equal("apache", config.Server.Type);
    }

    [Fact]
    public void Load_Parses_IniOverrides()
    {
        var config = SiteConfigLoader.Load(SampleToml);
        Assert.Equal("512M", config.Php.IniOverrides["memory_limit"]);
    }

    [Fact]
    public void RoundTrip_Preserves_Core_Fields()
    {
        var config = SiteConfigLoader.Load(SampleToml);
        var toml = SiteConfigLoader.Serialize(config);
        var config2 = SiteConfigLoader.Load(toml);

        Assert.Equal(config.Site.Hostname, config2.Site.Hostname);
        Assert.Equal(config.Php.Version, config2.Php.Version);
        Assert.Equal(config.Ssl.Enabled, config2.Ssl.Enabled);
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Core.Tests --filter "SiteConfigLoaderTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: TOML site config model and parser"
```

---

### Task 1.7: Create Daemon Worker Service Host

**Files:**
- `src/DevForge.Daemon/Program.cs` (modify)
- `src/DevForge.Daemon/DaemonService.cs` (create)
- `src/DevForge.Daemon/PidLock.cs` (create)

**Steps:**
- [ ] Replace Worker.cs template with `DaemonService : BackgroundService`
- [ ] Implement PID lock file management (SPEC.md Section 4, Daemon Lifecycle)
- [ ] Configure `Program.cs` with `Host.CreateDefaultBuilder` and services
- [ ] Register `DaemonService` as hosted service
- [ ] Add Serilog logging configuration
- [ ] Run daemon and verify PID file is created and cleaned up on exit

**Code:**
```csharp
// src/DevForge.Daemon/PidLock.cs
namespace DevForge.Daemon;

public class PidLock : IDisposable
{
    private readonly string _pidPath;
    private FileStream? _lockStream;

    public PidLock(string dataDir)
    {
        _pidPath = Path.Combine(dataDir, "daemon.pid");
    }

    public bool TryAcquire()
    {
        if (File.Exists(_pidPath))
        {
            var existingPid = int.Parse(File.ReadAllText(_pidPath).Trim());
            try
            {
                System.Diagnostics.Process.GetProcessById(existingPid);
                return false; // process is alive
            }
            catch (ArgumentException)
            {
                File.Delete(_pidPath); // stale PID
            }
        }

        _lockStream = new FileStream(_pidPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var pidBytes = System.Text.Encoding.UTF8.GetBytes(
            Environment.ProcessId.ToString());
        _lockStream.Write(pidBytes);
        _lockStream.Flush();
        return true;
    }

    public void Dispose()
    {
        _lockStream?.Dispose();
        try { File.Delete(_pidPath); } catch { }
    }
}
```

```csharp
// src/DevForge.Daemon/DaemonService.cs
using DevForge.Daemon.Data;

namespace DevForge.Daemon;

public class DaemonService : BackgroundService
{
    private readonly ILogger<DaemonService> _logger;
    private readonly DatabaseInitializer _dbInit;
    private PidLock? _pidLock;

    public DaemonService(ILogger<DaemonService> logger, DatabaseInitializer dbInit)
    {
        _logger = logger;
        _dbInit = dbInit;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dataDir = GetDataDir();
        Directory.CreateDirectory(dataDir);

        _pidLock = new PidLock(dataDir);
        if (!_pidLock.TryAcquire())
        {
            _logger.LogError("Another daemon instance is already running");
            return;
        }

        _logger.LogInformation("DevForge daemon starting, PID {Pid}", Environment.ProcessId);

        var dbResult = _dbInit.Initialize();
        if (!dbResult.Successful)
        {
            _logger.LogError("Database migration failed: {Error}", dbResult.ErrorMessage);
            return;
        }

        _logger.LogInformation("Database initialized, all migrations applied");

        // Main loop -- keep running until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("DevForge daemon shutting down");
        _pidLock.Dispose();
    }

    private static string GetDataDir()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevForge");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".devforge");
    }
}
```

```csharp
// src/DevForge.Daemon/Program.cs
using DevForge.Daemon;
using DevForge.Daemon.Data;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/devforge-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

var dataDir = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevForge")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".devforge");
var dbPath = Path.Combine(dataDir, "data", "state.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connStr = $"Data Source={dbPath}";

builder.Services.AddSingleton(new DatabaseInitializer(connStr));
builder.Services.AddHostedService<DaemonService>();

var host = builder.Build();
host.Run();
```

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/PidLockTests.cs
namespace DevForge.Daemon.Tests;

public class PidLockTests
{
    [Fact]
    public void TryAcquire_Succeeds_On_First_Call()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            using var pidLock = new PidLock(dir);
            Assert.True(pidLock.TryAcquire());
            Assert.True(File.Exists(Path.Combine(dir, "daemon.pid")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TryAcquire_Fails_When_Already_Locked()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            using var lock1 = new PidLock(dir);
            Assert.True(lock1.TryAcquire());

            using var lock2 = new PidLock(dir);
            Assert.False(lock2.TryAcquire());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Dispose_Removes_PidFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var pidPath = Path.Combine(dir, "daemon.pid");
        try
        {
            var pidLock = new PidLock(dir);
            pidLock.TryAcquire();
            pidLock.Dispose();
            Assert.False(File.Exists(pidPath));
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "PidLockTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: daemon worker service with PID lock and SQLite init"
```

---

### Task 1.8: Implement gRPC Server in Daemon

**Files:**
- `src/DevForge.Daemon/Grpc/DevForgeGrpcService.cs` (create)
- `src/DevForge.Daemon/Program.cs` (modify -- add gRPC + named pipe)

**Steps:**
- [ ] Create `DevForgeGrpcService` inheriting from `DevForgeService.DevForgeServiceBase`
- [ ] Implement `GetStatus` returning daemon version and uptime
- [ ] Configure Kestrel to listen on named pipe `devforge-daemon` (Windows) or Unix socket (macOS/Linux)
- [ ] Register gRPC service in DI
- [ ] Run daemon and verify named pipe is listening

**Code:**
```csharp
// src/DevForge.Daemon/Grpc/DevForgeGrpcService.cs
using DevForge.Proto;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DevForge.Daemon.Grpc;

public class DevForgeGrpcService : DevForgeService.DevForgeServiceBase
{
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly ILogger<DevForgeGrpcService> _logger;

    public DevForgeGrpcService(ILogger<DevForgeGrpcService> logger)
    {
        _logger = logger;
    }

    public override Task<DaemonStatus> GetStatus(Empty request, ServerCallContext context)
    {
        var uptime = (int)(DateTime.UtcNow - _startTime).TotalSeconds;
        return Task.FromResult(new DaemonStatus
        {
            Version = "0.1.0",
            Running = true,
            UptimeSeconds = uptime
        });
    }

    public override Task<ServiceListResponse> ListServices(Empty request, ServerCallContext context)
    {
        return Task.FromResult(new ServiceListResponse());
    }

    public override Task<ServiceResponse> StartService(ServiceRequest request, ServerCallContext context)
    {
        _logger.LogInformation("StartService requested for {ServiceId}", request.ServiceId);
        return Task.FromResult(new ServiceResponse
        {
            Success = false,
            Message = "Not implemented yet"
        });
    }

    public override Task<ServiceResponse> StopService(ServiceRequest request, ServerCallContext context)
    {
        return Task.FromResult(new ServiceResponse { Success = false, Message = "Not implemented yet" });
    }

    public override Task<ServiceResponse> RestartService(ServiceRequest request, ServerCallContext context)
    {
        return Task.FromResult(new ServiceResponse { Success = false, Message = "Not implemented yet" });
    }
}
```

```csharp
// src/DevForge.Daemon/Program.cs (updated)
using DevForge.Daemon;
using DevForge.Daemon.Data;
using DevForge.Daemon.Grpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/devforge-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSerilog();
builder.Services.AddGrpc();

builder.WebHost.ConfigureKestrel(options =>
{
    if (OperatingSystem.IsWindows())
    {
        options.ListenNamedPipe("devforge-daemon", listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
        });
    }
    else
    {
        var socketPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".devforge", "daemon.sock");
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (File.Exists(socketPath)) File.Delete(socketPath);
        options.ListenUnixSocket(socketPath, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
        });
    }
});

var dataDir = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevForge")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".devforge");
var dbPath = Path.Combine(dataDir, "data", "state.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddSingleton(new DatabaseInitializer($"Data Source={dbPath}"));
builder.Services.AddHostedService<DaemonService>();

var app = builder.Build();
app.MapGrpcService<DevForgeGrpcService>();
app.Run();
```

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/Grpc/DevForgeGrpcServiceTests.cs
using DevForge.Daemon.Grpc;
using DevForge.Proto;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevForge.Daemon.Tests.Grpc;

public class DevForgeGrpcServiceTests
{
    [Fact]
    public async Task GetStatus_Returns_Running()
    {
        var service = new DevForgeGrpcService(NullLogger<DevForgeGrpcService>.Instance);
        var result = await service.GetStatus(new Empty(), TestServerCallContext.Create());
        Assert.True(result.Running);
        Assert.Equal("0.1.0", result.Version);
        Assert.True(result.UptimeSeconds >= 0);
    }

    [Fact]
    public async Task ListServices_Returns_Empty_Initially()
    {
        var service = new DevForgeGrpcService(NullLogger<DevForgeGrpcService>.Instance);
        var result = await service.ListServices(new Empty(), TestServerCallContext.Create());
        Assert.Empty(result.Services);
    }
}
```

**Run:**
```bash
dotnet build DevForge.sln
dotnet test tests/DevForge.Daemon.Tests --filter "DevForgeGrpcServiceTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: gRPC server with named pipe transport and GetStatus"
```

---

### Task 1.9: Create gRPC Client Helper in Core

**Files:**
- `src/DevForge.Core/Client/DaemonClient.cs` (create)
- `src/DevForge.Core/Client/IDaemonClient.cs` (create)

**Steps:**
- [ ] Create `IDaemonClient` interface with typed methods wrapping gRPC calls
- [ ] Implement `DaemonClient` using `GrpcChannel` and named pipe / Unix socket handler
- [ ] This class is shared between Gui and Cli projects

**Code:**
```csharp
// src/DevForge.Core/Client/IDaemonClient.cs
using DevForge.Proto;

namespace DevForge.Core.Client;

public interface IDaemonClient : IDisposable
{
    Task<DaemonStatus> GetStatusAsync(CancellationToken ct = default);
    Task<ServiceListResponse> ListServicesAsync(CancellationToken ct = default);
    Task<ServiceResponse> StartServiceAsync(string serviceId, CancellationToken ct = default);
    Task<ServiceResponse> StopServiceAsync(string serviceId, CancellationToken ct = default);
    Task<ServiceResponse> RestartServiceAsync(string serviceId, CancellationToken ct = default);
    Task<SiteListResponse> ListSitesAsync(CancellationToken ct = default);
    Task<SiteResponse> CreateSiteAsync(CreateSiteRequest request, CancellationToken ct = default);
}
```

```csharp
// src/DevForge.Core/Client/DaemonClient.cs
using DevForge.Proto;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;

namespace DevForge.Core.Client;

public class DaemonClient : IDaemonClient
{
    private readonly GrpcChannel _channel;
    private readonly DevForgeService.DevForgeServiceClient _client;

    public DaemonClient()
    {
        var handler = CreateHandler();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
        _client = new DevForgeService.DevForgeServiceClient(_channel);
    }

    public async Task<DaemonStatus> GetStatusAsync(CancellationToken ct)
        => await _client.GetStatusAsync(new Empty(), cancellationToken: ct);

    public async Task<ServiceListResponse> ListServicesAsync(CancellationToken ct)
        => await _client.ListServicesAsync(new Empty(), cancellationToken: ct);

    public async Task<ServiceResponse> StartServiceAsync(string serviceId, CancellationToken ct)
        => await _client.StartServiceAsync(new ServiceRequest { ServiceId = serviceId }, cancellationToken: ct);

    public async Task<ServiceResponse> StopServiceAsync(string serviceId, CancellationToken ct)
        => await _client.StopServiceAsync(new ServiceRequest { ServiceId = serviceId }, cancellationToken: ct);

    public async Task<ServiceResponse> RestartServiceAsync(string serviceId, CancellationToken ct)
        => await _client.RestartServiceAsync(new ServiceRequest { ServiceId = serviceId }, cancellationToken: ct);

    public async Task<SiteListResponse> ListSitesAsync(CancellationToken ct)
        => await _client.ListSitesAsync(new Empty(), cancellationToken: ct);

    public async Task<SiteResponse> CreateSiteAsync(CreateSiteRequest request, CancellationToken ct)
        => await _client.CreateSiteAsync(request, cancellationToken: ct);

    public void Dispose() => _channel.Dispose();

    private static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new System.IO.Pipes.NamedPipeClientStream(
                        ".", "devforge-daemon",
                        System.IO.Pipes.PipeDirection.InOut,
                        System.IO.Pipes.PipeOptions.WriteThrough | System.IO.Pipes.PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(ct);
                    return pipe;
                }
                else
                {
                    var socketPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".devforge", "daemon.sock");
                    var socket = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.Unix,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Unspecified);
                    var endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath);
                    await socket.ConnectAsync(endpoint, ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
            }
        };
    }
}
```

**Test:**
```csharp
// tests/DevForge.Core.Tests/Client/DaemonClientTests.cs
namespace DevForge.Core.Tests.Client;

using DevForge.Core.Client;

public class DaemonClientTests
{
    [Fact]
    public void DaemonClient_Creates_Without_Exception()
    {
        // This test verifies construction; actual gRPC calls require running daemon
        using var client = new DaemonClient();
        Assert.NotNull(client);
    }
}
```

**Run:**
```bash
dotnet build DevForge.sln
```

**Commit:**
```bash
git add -A
git commit -m "feat: gRPC client wrapper with named pipe transport"
```

---

### Task 1.10: Create Basic Avalonia Window with Sidebar Layout

**Files:**
- `src/DevForge.Gui/App.axaml` (modify)
- `src/DevForge.Gui/App.axaml.cs` (modify)
- `src/DevForge.Gui/Views/MainWindow.axaml` (create)
- `src/DevForge.Gui/Views/MainWindow.axaml.cs` (create)
- `src/DevForge.Gui/ViewModels/MainWindowViewModel.cs` (create)
- `src/DevForge.Gui/Styles/DevForgeTokens.axaml` (create)

**Steps:**
- [ ] Configure `App.axaml` with `FluentTheme` dark mode and `DevForgeTokens.axaml`
- [ ] Create `MainWindow.axaml` with 3-row Grid: title bar, sidebar + content, status bar (per avalonia-ui-patterns.md Section 2)
- [ ] Create `MainWindowViewModel` with sidebar navigation
- [ ] Create `DevForgeTokens.axaml` with color primitives (per avalonia-ui-patterns.md Section 10)
- [ ] Run and verify dark window with sidebar appears

**Code:** (see `C:\work\sources\nks-ws\docs\plans\avalonia-ui-patterns.md` Sections 1, 2, 10 for complete AXAML and C# code)

**Code (`MainWindowViewModel.cs`):**
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevForge.Gui.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _daemonStatusText = "Connecting...";
    [ObservableProperty] private int _activeSiteCount;

    [RelayCommand]
    private void Navigate(string pageName)
    {
        // Placeholder -- pages will be added in Phase 4
        DaemonStatusText = $"Navigated to {pageName}";
    }
}
```

**Test:** Visual verification -- dark window renders with sidebar containing Dashboard/Sites/Settings buttons.

**Run:**
```bash
dotnet run --project src/DevForge.Gui
```

**Commit:**
```bash
git add -A
git commit -m "feat: avalonia main window with sidebar and dark theme"
```

---

### Task 1.11: Create CLI with System.CommandLine -- `devforge status`

**Files:**
- `src/DevForge.Cli/Program.cs` (modify)
- `src/DevForge.Cli/Commands/StatusCommand.cs` (create)

**Steps:**
- [ ] Configure System.CommandLine root command in Program.cs
- [ ] Implement `devforge status` command that connects to daemon via gRPC and prints status
- [ ] Add `--json` global option
- [ ] Use Spectre.Console for table output formatting
- [ ] Test with daemon running

**Code:**
```csharp
// src/DevForge.Cli/Program.cs
using System.CommandLine;
using DevForge.Cli.Commands;

var rootCommand = new RootCommand("DevForge - Local development server manager");

var jsonOption = new Option<bool>("--json", "Output in JSON format");
rootCommand.AddGlobalOption(jsonOption);

rootCommand.AddCommand(StatusCommand.Create(jsonOption));

return await rootCommand.InvokeAsync(args);
```

```csharp
// src/DevForge.Cli/Commands/StatusCommand.cs
using System.CommandLine;
using System.Text.Json;
using DevForge.Core.Client;
using Spectre.Console;

namespace DevForge.Cli.Commands;

public static class StatusCommand
{
    public static Command Create(Option<bool> jsonOption)
    {
        var command = new Command("status", "Show daemon and service status");
        command.SetHandler(async (bool json) =>
        {
            try
            {
                using var client = new DaemonClient();
                var status = await client.GetStatusAsync();

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        success = true,
                        data = new
                        {
                            version = status.Version,
                            running = status.Running,
                            uptime_seconds = status.UptimeSeconds,
                            services = status.Services.Select(s => new
                            {
                                id = s.ServiceId,
                                state = s.State,
                                pid = s.Pid
                            })
                        }
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    AnsiConsole.MarkupLine($"[green]DevForge v{status.Version}[/]");
                    AnsiConsole.MarkupLine($"  Status: [green]Running[/]");
                    AnsiConsole.MarkupLine($"  Uptime: {TimeSpan.FromSeconds(status.UptimeSeconds)}");

                    if (status.Services.Count > 0)
                    {
                        var table = new Table();
                        table.AddColumn("Service");
                        table.AddColumn("State");
                        table.AddColumn("PID");
                        foreach (var svc in status.Services)
                            table.AddRow(svc.DisplayName, svc.State, svc.Pid.ToString());
                        AnsiConsole.Write(table);
                    }
                }
            }
            catch (Exception ex)
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new { error = true, message = ex.Message }));
                else
                    AnsiConsole.MarkupLine($"[red]Error:[/] Daemon not running ({ex.Message})");
                Environment.ExitCode = 1;
            }
        }, jsonOption);
        return command;
    }
}
```

**Test:**
```csharp
// tests/DevForge.Core.Tests/Cli/StatusCommandTests.cs
namespace DevForge.Core.Tests.Cli;

public class StatusCommandTests
{
    [Fact]
    public void Command_Has_Status_Subcommand()
    {
        // Verify the CLI structure compiles and status command exists
        var rootCommand = new System.CommandLine.RootCommand("test");
        var jsonOption = new System.CommandLine.Option<bool>("--json");
        rootCommand.AddCommand(DevForge.Cli.Commands.StatusCommand.Create(jsonOption));
        Assert.Single(rootCommand.Subcommands);
        Assert.Equal("status", rootCommand.Subcommands[0].Name);
    }
}
```

**Run:**
```bash
dotnet build src/DevForge.Cli
# With daemon running: dotnet run --project src/DevForge.Cli -- status
```

**Commit:**
```bash
git add -A
git commit -m "feat: CLI with status command and json output"
```

---

### Task 1.12: Wire CLI to Daemon End-to-End Flow

**Files:**
- `src/DevForge.Cli/Commands/ServiceCommand.cs` (create)

**Steps:**
- [ ] Add `devforge start [service]` and `devforge stop [service]` commands
- [ ] Wire to gRPC StartService/StopService RPC calls
- [ ] Verify: start daemon in one terminal, run `devforge status` in another
- [ ] Confirm gRPC response is received and printed

**Code:**
```csharp
// src/DevForge.Cli/Commands/ServiceCommand.cs
using System.CommandLine;
using DevForge.Core.Client;
using Spectre.Console;

namespace DevForge.Cli.Commands;

public static class ServiceCommand
{
    public static Command Create(Option<bool> jsonOption)
    {
        var command = new Command("start", "Start a service or all services");
        var serviceArg = new Argument<string?>("service", () => null, "Service name (omit for all)");
        command.AddArgument(serviceArg);
        command.SetHandler(async (string? service, bool json) =>
        {
            using var client = new DaemonClient();
            if (service is not null)
            {
                var result = await client.StartServiceAsync(service);
                if (!json)
                    AnsiConsole.MarkupLine(result.Success
                        ? $"[green]Started {service}[/]"
                        : $"[red]Failed: {result.Message}[/]");
            }
            else
            {
                var services = await client.ListServicesAsync();
                foreach (var svc in services.Services)
                    await client.StartServiceAsync(svc.ServiceId);
                if (!json)
                    AnsiConsole.MarkupLine("[green]All services started[/]");
            }
        }, serviceArg, jsonOption);
        return command;
    }

    public static Command CreateStop(Option<bool> jsonOption)
    {
        var command = new Command("stop", "Stop a service or all services");
        var serviceArg = new Argument<string?>("service", () => null, "Service name");
        command.AddArgument(serviceArg);
        command.SetHandler(async (string? service, bool json) =>
        {
            using var client = new DaemonClient();
            if (service is not null)
            {
                var result = await client.StopServiceAsync(service);
                if (!json)
                    AnsiConsole.MarkupLine(result.Success
                        ? $"[green]Stopped {service}[/]"
                        : $"[red]Failed: {result.Message}[/]");
            }
        }, serviceArg, jsonOption);
        return command;
    }
}
```

**Test:** Manual integration test: run daemon in terminal 1, run `devforge status` in terminal 2.

**Run:**
```bash
# Terminal 1:
dotnet run --project src/DevForge.Daemon
# Terminal 2:
dotnet run --project src/DevForge.Cli -- status
```

**Commit:**
```bash
git add -A
git commit -m "feat: CLI start/stop commands with daemon integration"
```

---

### Task 1.13: Add Serilog Structured Logging

**Files:**
- `src/DevForge.Daemon/Program.cs` (modify -- already partially done in 1.7)
- `src/DevForge.Daemon/appsettings.json` (create)

**Steps:**
- [ ] Configure Serilog with structured JSON output
- [ ] Add rolling file sink to `~/.devforge/log/devforge-{Date}.log`
- [ ] Add console sink with colorized output
- [ ] Add request logging middleware for gRPC calls
- [ ] Verify log file is created when daemon starts

**Code (`appsettings.json`):**
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Grpc": "Warning"
      }
    }
  }
}
```

**Test:** Verify log file exists after daemon start.

**Run:**
```bash
dotnet run --project src/DevForge.Daemon
# Check that log file exists in logs/ directory
```

**Commit:**
```bash
git add -A
git commit -m "feat: structured serilog logging with file rotation"
```

---

### Task 1.14: Integration Test -- Daemon Starts, CLI Connects

**Files:**
- `tests/DevForge.Daemon.Tests/Integration/DaemonIntegrationTests.cs` (create)

**Steps:**
- [ ] Write integration test that starts daemon in-process
- [ ] Connect via gRPC client
- [ ] Call GetStatus and assert Running = true
- [ ] Shut down daemon gracefully
- [ ] Assert PID file is cleaned up

**Code:**
```csharp
// tests/DevForge.Daemon.Tests/Integration/DaemonIntegrationTests.cs
using DevForge.Proto;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using DevForge.Daemon.Data;
using DevForge.Daemon.Grpc;

namespace DevForge.Daemon.Tests.Integration;

public class DaemonIntegrationTests : IAsyncLifetime
{
    private WebApplication? _app;
    private readonly string _pipeName = $"devforge-test-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(new DatabaseInitializer("Data Source=:memory:"));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenNamedPipe(_pipeName, o => o.Protocols = HttpProtocols.Http2);
        });

        _app = builder.Build();
        _app.MapGrpcService<DevForgeGrpcService>();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    [Fact]
    public async Task GetStatus_Returns_Running_Via_NamedPipe()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".", _pipeName,
                    System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.WriteThrough | System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ct);
                return pipe;
            }
        };

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });

        var client = new DevForgeService.DevForgeServiceClient(channel);
        var status = await client.GetStatusAsync(new Empty());

        Assert.True(status.Running);
        Assert.Equal("0.1.0", status.Version);
        Assert.True(status.UptimeSeconds >= 0);
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "DaemonIntegrationTests"
```

**Commit:**
```bash
git add -A
git commit -m "test: daemon integration test with gRPC over named pipe"
```

---

## Phase 2: Core Services

**Purpose:** Implement process management, service lifecycle state machine, and Apache/MySQL/PHP-FPM modules with health monitoring. (SPEC.md Section 22, Phase 2; Section 5 & 9)

---

### Task 2.1: Implement ServiceUnit State Machine

**Files:**
- `src/DevForge.Daemon/Services/ServiceUnit.cs` (create)
- `tests/DevForge.Daemon.Tests/Services/ServiceUnitTests.cs` (create)

**Steps:**
- [ ] Create `ServiceUnit` class with state machine from SPEC.md Section 5.1
- [ ] Implement state transitions: Stopped->Starting->Running, Running->Stopping->Stopped, Running->Crashed->Restarting->Starting
- [ ] Add guards for invalid transitions (throw InvalidOperationException)
- [ ] Track RestartCount, LastCrash, Pid
- [ ] Write tests for all valid and invalid transitions

**Code:**
```csharp
// src/DevForge.Daemon/Services/ServiceUnit.cs
using DevForge.Core.Interfaces;
using DevForge.Core.Models;

namespace DevForge.Daemon.Services;

public class ServiceUnit
{
    public string Id { get; }
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public int? Pid { get; set; }
    public Process? Process { get; set; }
    public int RestartCount { get; private set; }
    public DateTime? LastCrash { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public IServiceModule Module { get; }
    public RestartPolicy RestartPolicy { get; set; } = new();

    public ServiceUnit(string id, IServiceModule module)
    {
        Id = id;
        Module = module;
    }

    public void TransitionTo(ServiceState newState)
    {
        if (!IsValidTransition(State, newState))
            throw new InvalidOperationException(
                $"Invalid state transition: {State} -> {newState}");

        var old = State;
        State = newState;

        switch (newState)
        {
            case ServiceState.Running:
                StartedAt = DateTime.UtcNow;
                break;
            case ServiceState.Crashed:
                LastCrash = DateTime.UtcNow;
                break;
            case ServiceState.Restarting:
                RestartCount++;
                break;
            case ServiceState.Stopped:
                Pid = null;
                Process = null;
                break;
        }
    }

    public void ResetRestartCount() => RestartCount = 0;

    public bool ShouldRestart()
    {
        if (State != ServiceState.Crashed) return false;
        if (RestartCount >= RestartPolicy.MaxRestarts) return false;
        if (LastCrash.HasValue &&
            DateTime.UtcNow - LastCrash.Value > RestartPolicy.Window)
        {
            RestartCount = 0;
        }
        return RestartCount < RestartPolicy.MaxRestarts;
    }

    public TimeSpan GetNextBackoff()
        => RestartPolicy.GetBackoff(RestartCount);

    private static bool IsValidTransition(ServiceState from, ServiceState to) => (from, to) switch
    {
        (ServiceState.Stopped, ServiceState.Starting) => true,
        (ServiceState.Starting, ServiceState.Running) => true,
        (ServiceState.Starting, ServiceState.Crashed) => true,
        (ServiceState.Running, ServiceState.Stopping) => true,
        (ServiceState.Running, ServiceState.Crashed) => true,
        (ServiceState.Stopping, ServiceState.Stopped) => true,
        (ServiceState.Crashed, ServiceState.Restarting) => true,
        (ServiceState.Crashed, ServiceState.Disabled) => true,
        (ServiceState.Crashed, ServiceState.Stopped) => true,
        (ServiceState.Restarting, ServiceState.Starting) => true,
        (ServiceState.Disabled, ServiceState.Stopped) => true,
        _ => false
    };
}
```

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/Services/ServiceUnitTests.cs
using DevForge.Core.Models;
using DevForge.Daemon.Services;
using Moq;
using DevForge.Core.Interfaces;

namespace DevForge.Daemon.Tests.Services;

public class ServiceUnitTests
{
    private ServiceUnit CreateUnit()
    {
        var mock = new Mock<IServiceModule>();
        mock.Setup(m => m.ServiceId).Returns("test");
        return new ServiceUnit("test", mock.Object);
    }

    [Fact]
    public void Initial_State_Is_Stopped()
    {
        var unit = CreateUnit();
        Assert.Equal(ServiceState.Stopped, unit.State);
    }

    [Fact]
    public void Valid_Transition_Stopped_To_Starting()
    {
        var unit = CreateUnit();
        unit.TransitionTo(ServiceState.Starting);
        Assert.Equal(ServiceState.Starting, unit.State);
    }

    [Fact]
    public void Valid_Transition_Starting_To_Running()
    {
        var unit = CreateUnit();
        unit.TransitionTo(ServiceState.Starting);
        unit.TransitionTo(ServiceState.Running);
        Assert.Equal(ServiceState.Running, unit.State);
        Assert.NotNull(unit.StartedAt);
    }

    [Fact]
    public void Invalid_Transition_Throws()
    {
        var unit = CreateUnit();
        Assert.Throws<InvalidOperationException>(() =>
            unit.TransitionTo(ServiceState.Running));
    }

    [Fact]
    public void Crash_Sets_LastCrash()
    {
        var unit = CreateUnit();
        unit.TransitionTo(ServiceState.Starting);
        unit.TransitionTo(ServiceState.Running);
        unit.TransitionTo(ServiceState.Crashed);
        Assert.NotNull(unit.LastCrash);
    }

    [Fact]
    public void Restart_Increments_Count()
    {
        var unit = CreateUnit();
        unit.TransitionTo(ServiceState.Starting);
        unit.TransitionTo(ServiceState.Running);
        unit.TransitionTo(ServiceState.Crashed);
        unit.TransitionTo(ServiceState.Restarting);
        Assert.Equal(1, unit.RestartCount);
    }

    [Fact]
    public void ShouldRestart_False_When_MaxReached()
    {
        var unit = CreateUnit();
        unit.RestartPolicy = new RestartPolicy { MaxRestarts = 2 };
        for (int i = 0; i < 3; i++)
        {
            unit.TransitionTo(ServiceState.Starting);
            unit.TransitionTo(ServiceState.Running);
            unit.TransitionTo(ServiceState.Crashed);
            if (unit.ShouldRestart())
                unit.TransitionTo(ServiceState.Restarting);
        }
        Assert.False(unit.ShouldRestart());
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "ServiceUnitTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: ServiceUnit state machine with restart policy"
```

---

### Task 2.2: Implement ProcessManager

**Files:**
- `src/DevForge.Daemon/Services/ProcessManager.cs` (create)
- `tests/DevForge.Daemon.Tests/Services/ProcessManagerTests.cs` (create)

**Steps:**
- [ ] Create `ProcessManager` that tracks `ServiceUnit` instances
- [ ] Implement `RegisterModule`, `StartService`, `StopService`, `GetServiceStatus`
- [ ] Use `System.Diagnostics.Process` for starting processes (per csharp-process-management.md)
- [ ] Wire `Exited` event to state machine crash detection
- [ ] Implement graceful shutdown with timeout fallback to Kill

**Code:**
```csharp
// src/DevForge.Daemon/Services/ProcessManager.cs
using DevForge.Core.Interfaces;
using DevForge.Core.Models;

namespace DevForge.Daemon.Services;

public class ProcessManager
{
    private readonly Dictionary<string, ServiceUnit> _units = new();
    private readonly ILogger<ProcessManager> _logger;
    private readonly nint _jobHandle;

    public ProcessManager(ILogger<ProcessManager> logger)
    {
        _logger = logger;
        _jobHandle = OperatingSystem.IsWindows() ? JobObjects.CreateKillOnCloseJob() : nint.Zero;
    }

    public void RegisterModule(IServiceModule module)
    {
        var unit = new ServiceUnit(module.ServiceId, module);
        _units[module.ServiceId] = unit;
        _logger.LogInformation("Registered service module: {ServiceId}", module.ServiceId);
    }

    public ServiceUnit? GetUnit(string serviceId)
        => _units.TryGetValue(serviceId, out var unit) ? unit : null;

    public IReadOnlyList<ServiceUnit> GetAllUnits()
        => _units.Values.ToList();

    public async Task StartServiceAsync(string serviceId, CancellationToken ct)
    {
        var unit = _units[serviceId];
        unit.TransitionTo(ServiceState.Starting);
        try
        {
            await unit.Module.StartAsync(ct);
            unit.TransitionTo(ServiceState.Running);
            unit.ResetRestartCount();
            _logger.LogInformation("Service {ServiceId} started", serviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Service {ServiceId} failed to start", serviceId);
            unit.TransitionTo(ServiceState.Crashed);
        }
    }

    public async Task StopServiceAsync(string serviceId, CancellationToken ct)
    {
        var unit = _units[serviceId];
        if (unit.State != ServiceState.Running) return;

        unit.TransitionTo(ServiceState.Stopping);
        try
        {
            await unit.Module.StopAsync(ct);
            unit.TransitionTo(ServiceState.Stopped);
            _logger.LogInformation("Service {ServiceId} stopped", serviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Service {ServiceId} failed to stop cleanly", serviceId);
            unit.TransitionTo(ServiceState.Stopped);
        }
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        // Shutdown in reverse order: web servers -> PHP -> MySQL -> others
        var ordered = _units.Values
            .Where(u => u.State == ServiceState.Running)
            .OrderBy(u => u.Module.Type switch
            {
                ServiceType.WebServer => 1,
                ServiceType.PhpRuntime => 2,
                ServiceType.Database => 3,
                _ => 4
            });
        foreach (var unit in ordered)
            await StopServiceAsync(unit.Id, ct);
    }
}
```

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/Services/ProcessManagerTests.cs
using DevForge.Core.Interfaces;
using DevForge.Core.Models;
using DevForge.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DevForge.Daemon.Tests.Services;

public class ProcessManagerTests
{
    [Fact]
    public async Task StartService_Transitions_To_Running()
    {
        var module = new Mock<IServiceModule>();
        module.Setup(m => m.ServiceId).Returns("test");
        module.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var pm = new ProcessManager(NullLogger<ProcessManager>.Instance);
        pm.RegisterModule(module.Object);

        await pm.StartServiceAsync("test", CancellationToken.None);
        Assert.Equal(ServiceState.Running, pm.GetUnit("test")!.State);
    }

    [Fact]
    public async Task StopService_Transitions_To_Stopped()
    {
        var module = new Mock<IServiceModule>();
        module.Setup(m => m.ServiceId).Returns("test");
        module.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        module.Setup(m => m.StopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var pm = new ProcessManager(NullLogger<ProcessManager>.Instance);
        pm.RegisterModule(module.Object);

        await pm.StartServiceAsync("test", CancellationToken.None);
        await pm.StopServiceAsync("test", CancellationToken.None);
        Assert.Equal(ServiceState.Stopped, pm.GetUnit("test")!.State);
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "ProcessManagerTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: ProcessManager with service lifecycle coordination"
```

---

### Task 2.3: Implement Windows Job Objects

**Files:**
- `src/DevForge.Daemon/Services/JobObjects.cs` (create)
- `tests/DevForge.Daemon.Tests/Services/JobObjectsTests.cs` (create)

**Steps:**
- [ ] Create `JobObjects` class with P/Invoke for Windows kernel32 (per csharp-process-management.md Section 3)
- [ ] Implement `CreateKillOnCloseJob()` and `AssignProcess()`
- [ ] Guard with `OperatingSystem.IsWindows()` check
- [ ] Write test that creates a Job Object handle (Windows only)

**Code:** (use complete code from `C:\work\sources\nks-ws\docs\plans\csharp-process-management.md` Section 3, `JobObjects` class)

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/Services/JobObjectsTests.cs
namespace DevForge.Daemon.Tests.Services;

public class JobObjectsTests
{
    [Fact]
    [Trait("Category", "Windows")]
    public void CreateKillOnCloseJob_Returns_Valid_Handle()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(true, "Skipped on non-Windows");
            return;
        }
        var handle = DevForge.Daemon.Services.JobObjects.CreateKillOnCloseJob();
        Assert.NotEqual(nint.Zero, handle);
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "JobObjectsTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: windows job objects for child process cleanup"
```

---

### Task 2.4: Implement TemplateEngine (Scriban)

**Files:**
- `src/DevForge.Daemon/Config/TemplateEngine.cs` (create)
- `src/DevForge.Daemon/Config/Templates/apache-vhost.conf` (create)
- `tests/DevForge.Daemon.Tests/Config/TemplateEngineTests.cs` (create)

**Steps:**
- [ ] Create `TemplateEngine` that loads Scriban templates from embedded resources
- [ ] Create Apache vhost template from SPEC.md Section 8
- [ ] Implement `RenderAsync(templateName, model)` method
- [ ] Handle platform-specific PHP handler (Unix socket vs Windows TCP per csharp-process-management.md Section 8)
- [ ] Write test rendering a Nette site template and asserting output contains correct directives

**Code:**
```csharp
// src/DevForge.Daemon/Config/TemplateEngine.cs
using Scriban;
using Scriban.Runtime;
using DevForge.Core.Configuration;

namespace DevForge.Daemon.Config;

public class TemplateEngine
{
    private readonly Dictionary<string, Template> _templates = new();

    public TemplateEngine()
    {
        LoadEmbeddedTemplates();
    }

    public async Task<string> RenderAsync(string templateName, SiteConfig config, CancellationToken ct)
    {
        if (!_templates.TryGetValue(templateName, out var template))
            throw new FileNotFoundException($"Template not found: {templateName}");

        var scriptObject = new ScriptObject();
        scriptObject.Import(new
        {
            site = config.Site,
            php = config.Php,
            ssl = config.Ssl,
            server = config.Server,
            is_windows = OperatingSystem.IsWindows(),
            php_fpm_socket = GetPhpFpmSocket(config.Php.Version),
            php_fpm_port = GetPhpFpmPort(config.Php.Version),
            now = DateTime.UtcNow.ToString("o")
        });

        var context = new TemplateContext();
        context.PushGlobal(scriptObject);
        return await Task.FromResult(template.Render(context));
    }

    private static string GetPhpFpmSocket(string phpVersion)
        => $"~/.devforge/run/php-fpm-{phpVersion}.sock";

    private static int GetPhpFpmPort(string phpVersion) => phpVersion switch
    {
        "5.6" => 9056, "7.0" => 9070, "7.4" => 9074,
        "8.0" => 9080, "8.1" => 9081, "8.2" => 9082,
        "8.3" => 9083, "8.4" => 9084,
        _ => 9080 + int.Parse(phpVersion.Split('.')[1])
    };

    private void LoadEmbeddedTemplates()
    {
        var assembly = typeof(TemplateEngine).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Templates.")))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var key = Path.GetFileName(name.Replace("DevForge.Daemon.Config.Templates.", ""));
            _templates[key] = Template.Parse(reader.ReadToEnd());
        }
    }
}
```

**Code (`apache-vhost.conf` template -- embed as EmbeddedResource):**
```
# Generated by DevForge - DO NOT EDIT MANUALLY
# Source: {{ site.hostname }}.toml  Generated: {{ now }}

<VirtualHost *:80>
    ServerName {{ site.hostname }}
    {{ for alias in site.aliases }}
    ServerAlias {{ alias }}
    {{ end }}
    DocumentRoot "{{ site.document_root }}"
    {{ if php.version }}
    {{ if is_windows }}
    <FilesMatch "\.php$">
        SetHandler "proxy:fcgi://127.0.0.1:{{ php_fpm_port }}"
    </FilesMatch>
    {{ else }}
    <FilesMatch "\.php$">
        SetHandler "proxy:unix:{{ php_fpm_socket }}|fcgi://localhost"
    </FilesMatch>
    {{ end }}
    {{ end }}
    {{ server.custom_directives }}
</VirtualHost>

{{ if ssl.enabled }}
<VirtualHost *:443>
    ServerName {{ site.hostname }}
    {{ for alias in site.aliases }}
    ServerAlias {{ alias }}
    {{ end }}
    DocumentRoot "{{ site.document_root }}"
    SSLEngine on
    SSLCertificateFile "{{ ssl.cert_path }}"
    SSLCertificateKeyFile "{{ ssl.key_path }}"
    {{ if php.version }}
    {{ if is_windows }}
    <FilesMatch "\.php$">
        SetHandler "proxy:fcgi://127.0.0.1:{{ php_fpm_port }}"
    </FilesMatch>
    {{ else }}
    <FilesMatch "\.php$">
        SetHandler "proxy:unix:{{ php_fpm_socket }}|fcgi://localhost"
    </FilesMatch>
    {{ end }}
    {{ end }}
    {{ server.custom_directives }}
</VirtualHost>
{{ end }}
```

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/Config/TemplateEngineTests.cs
using DevForge.Core.Configuration;
using DevForge.Daemon.Config;

namespace DevForge.Daemon.Tests.Config;

public class TemplateEngineTests
{
    [Fact]
    public async Task Render_Apache_Vhost_Contains_ServerName()
    {
        var engine = new TemplateEngine();
        var config = new SiteConfig
        {
            Site = { Hostname = "myapp.loc", DocumentRoot = "/var/www/myapp" },
            Php = { Version = "8.2" }
        };
        var result = await engine.RenderAsync("apache-vhost.conf", config, CancellationToken.None);
        Assert.Contains("ServerName myapp.loc", result);
        Assert.Contains("DocumentRoot", result);
    }

    [Fact]
    public async Task Render_Apache_Vhost_Has_PHP_Handler()
    {
        var engine = new TemplateEngine();
        var config = new SiteConfig
        {
            Site = { Hostname = "test.loc", DocumentRoot = "/www" },
            Php = { Version = "8.3" }
        };
        var result = await engine.RenderAsync("apache-vhost.conf", config, CancellationToken.None);
        Assert.Contains(".php", result);
        Assert.Contains("SetHandler", result);
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "TemplateEngineTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: Scriban template engine with Apache vhost template"
```

---

### Task 2.5: Implement ConfigValidator

**Files:**
- `src/DevForge.Daemon/Config/ConfigValidator.cs` (create)
- `tests/DevForge.Daemon.Tests/Config/ConfigValidatorTests.cs` (create)

**Steps:**
- [ ] Create `ConfigValidator` using CliWrap to run `httpd -t -f configPath`
- [ ] Parse exit code and stderr for validation result
- [ ] Return `ValidationResult` with error messages
- [ ] Write test with a mock that simulates httpd -t output

**Code:**
```csharp
// src/DevForge.Daemon/Config/ConfigValidator.cs
using CliWrap;
using CliWrap.Buffered;
using DevForge.Core.Models;

namespace DevForge.Daemon.Config;

public class ConfigValidator
{
    private readonly string _httpdPath;

    public ConfigValidator(string httpdPath = "httpd")
    {
        _httpdPath = httpdPath;
    }

    public async Task<ValidationResult> ValidateApacheConfigAsync(string configPath, CancellationToken ct)
    {
        try
        {
            var result = await Cli.Wrap(_httpdPath)
                .WithArguments(["-t", "-f", configPath])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode == 0)
                return ValidationResult.Success();

            var errors = result.StandardError
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("Syntax error", StringComparison.OrdinalIgnoreCase)
                         || l.Contains("Error", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return ValidationResult.Failure(errors.Length > 0
                ? errors
                : [$"httpd -t failed with exit code {result.ExitCode}: {result.StandardError}"]);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure($"Validation failed: {ex.Message}");
        }
    }
}
```

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/Config/ConfigValidatorTests.cs
using DevForge.Daemon.Config;

namespace DevForge.Daemon.Tests.Config;

public class ConfigValidatorTests
{
    [Fact]
    public async Task Validate_NonExistent_Binary_Returns_Failure()
    {
        var validator = new ConfigValidator("/nonexistent/httpd");
        var result = await validator.ValidateApacheConfigAsync("/tmp/test.conf", CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "ConfigValidatorTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: config validator with httpd -t via CliWrap"
```

---

### Task 2.6: Implement AtomicWriter

**Files:**
- `src/DevForge.Daemon/Config/AtomicWriter.cs` (create)
- `tests/DevForge.Daemon.Tests/Config/AtomicWriterTests.cs` (create)

**Steps:**
- [ ] Implement atomic write: write to .tmp, validate, rename to target
- [ ] Archive previous config to history/ (keep last 5 versions per SPEC.md Section 8)
- [ ] Rollback: delete .tmp if validation fails
- [ ] Write tests for successful write, failed validation, and history rotation

**Code:**
```csharp
// src/DevForge.Daemon/Config/AtomicWriter.cs
namespace DevForge.Daemon.Config;

public class AtomicWriter
{
    private const int MaxHistoryVersions = 5;

    public async Task WriteAtomicAsync(string targetPath, string content,
        Func<string, CancellationToken, Task<Core.Models.ValidationResult>>? validator = null,
        CancellationToken ct = default)
    {
        var tmpPath = targetPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, content, ct);

        if (validator is not null)
        {
            var result = await validator(tmpPath, ct);
            if (!result.IsValid)
            {
                File.Delete(tmpPath);
                throw new InvalidOperationException(
                    $"Validation failed: {string.Join("; ", result.Errors)}");
            }
        }

        if (File.Exists(targetPath))
            ArchiveConfig(targetPath);

        File.Move(tmpPath, targetPath, overwrite: true);
    }

    private static void ArchiveConfig(string configPath)
    {
        var dir = Path.GetDirectoryName(configPath)!;
        var historyDir = Path.Combine(dir, "history");
        Directory.CreateDirectory(historyDir);

        var baseName = Path.GetFileName(configPath);

        // Shift existing versions
        for (int i = MaxHistoryVersions; i >= 2; i--)
        {
            var older = Path.Combine(historyDir, $"{baseName}.{i - 1}");
            var newer = Path.Combine(historyDir, $"{baseName}.{i}");
            if (File.Exists(older))
            {
                if (i == MaxHistoryVersions && File.Exists(newer))
                    File.Delete(newer);
                File.Move(older, newer, overwrite: true);
            }
        }

        // Archive current as .1
        File.Copy(configPath, Path.Combine(historyDir, $"{baseName}.1"), overwrite: true);
    }
}
```

**Test:**
```csharp
// tests/DevForge.Daemon.Tests/Config/AtomicWriterTests.cs
using DevForge.Core.Models;
using DevForge.Daemon.Config;

namespace DevForge.Daemon.Tests.Config;

public class AtomicWriterTests
{
    [Fact]
    public async Task WriteAtomic_Creates_File()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "test.conf");
        try
        {
            var writer = new AtomicWriter();
            await writer.WriteAtomicAsync(target, "ServerName test.loc");
            Assert.True(File.Exists(target));
            Assert.Equal("ServerName test.loc", await File.ReadAllTextAsync(target));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WriteAtomic_Rolls_Back_On_Validation_Failure()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "test.conf");
        try
        {
            var writer = new AtomicWriter();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await writer.WriteAtomicAsync(target, "bad content",
                    (_, _) => Task.FromResult(ValidationResult.Failure("syntax error"))));
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WriteAtomic_Archives_Previous_Version()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "test.conf");
        try
        {
            var writer = new AtomicWriter();
            await writer.WriteAtomicAsync(target, "version 1");
            await writer.WriteAtomicAsync(target, "version 2");

            Assert.Equal("version 2", await File.ReadAllTextAsync(target));
            var historyPath = Path.Combine(dir, "history", "test.conf.1");
            Assert.True(File.Exists(historyPath));
            Assert.Equal("version 1", await File.ReadAllTextAsync(historyPath));
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

**Run:**
```bash
dotnet test tests/DevForge.Daemon.Tests --filter "AtomicWriterTests"
```

**Commit:**
```bash
git add -A
git commit -m "feat: atomic config writer with history rotation"
```

---

### Task 2.7 - 2.12: Remaining Core Services

Due to the length of this document, tasks 2.7 through 2.12 follow the same pattern. Each is detailed below with summary specifications.

---

### Task 2.7: Implement ApacheModule

**Files:** `src/DevForge.Daemon/Modules/ApacheModule.cs`, test file

Implement `IServiceModule` for Apache. Start via `Process.Start("httpd")`, stop via `httpd -k stop` (Windows) or `apachectl graceful-stop` (Unix). Health check: TCP port 80 open. Use CliWrap for `httpd -t` validation. Reference: csharp-process-management.md Section 2, SPEC.md Section 9.

**Commit:** `git commit -m "feat: apache service module with start/stop/validate"`

---

### Task 2.8: Implement MySqlModule

**Files:** `src/DevForge.Daemon/Modules/MySqlModule.cs`, test file

Implement `IServiceModule` for MySQL. Start via `Process.Start("mysqld", "--standalone")`. Stop via CliWrap `mysqladmin shutdown`. Health check: `mysqladmin ping`. Init data dir on first run: `mysqld --initialize-insecure`. Reference: csharp-process-management.md Sections 2 & 5.

**Commit:** `git commit -m "feat: mysql service module with init and health check"`

---

### Task 2.9: Implement PhpFpmModule

**Files:** `src/DevForge.Daemon/Modules/PhpFpmModule.cs`, test file

Implement `IServiceModule` for PHP-FPM (Unix) / php-cgi.exe (Windows). Multi-version support with deterministic port allocation (9056, 9074, 9082, etc. per SPEC.md Section 9). Per-site pool config generation. Reference: csharp-process-management.md Section 8.

**Commit:** `git commit -m "feat: php-fpm module with multi-version support"`

---

### Task 2.10: Implement HealthMonitor

**Files:** `src/DevForge.Daemon/Services/HealthMonitor.cs`, test file

Background task polling every 5 seconds. Check PID alive, TCP port open, service-specific checks. Trigger restart policy on failure. Collect metrics (CPU%, RAM). Reference: csharp-process-management.md Section 5.

**Commit:** `git commit -m "feat: health monitor with auto-restart on crash"`

---

### Task 2.11: Implement MetricsCollector

**Files:** `src/DevForge.Daemon/Services/MetricsCollector.cs`, test file

Collect `Process.TotalProcessorTime` and `Process.WorkingSet64` per service. Expose via gRPC `StreamMetrics`. Use `Channel<T>` for bounded pub/sub.

**Commit:** `git commit -m "feat: metrics collector with process CPU and RAM"`

---

### Task 2.12: Wire gRPC Service Management RPCs

**Files:** `src/DevForge.Daemon/Grpc/DevForgeGrpcService.cs` (modify)

Implement `StartService`, `StopService`, `RestartService`, `ListServices`, `GetServiceStatus` RPCs by delegating to `ProcessManager`. Return proper gRPC status codes for errors (SPEC.md Section 7 error table).

**Commit:** `git commit -m "feat: gRPC service management RPCs"`

---

## Phase 3: Sites + DNS + SSL

**Purpose:** Full site lifecycle -- TOML config, template rendering, hosts file management, SSL certificates. (SPEC.md Sections 8, 11, 12, 13)

---

### Task 3.1: Implement VirtualHostManager

**Files:** `src/DevForge.Daemon/Config/VirtualHostManager.cs`, test file

CRUD for sites: create TOML file at `~/.devforge/sites/{domain}.toml`, insert SQLite row, run config pipeline. Reference: SPEC.md Section 11 create site flow (10-step sequence).

**Commit:** `git commit -m "feat: virtual host manager with TOML and SQLite sync"`

---

### Task 3.2: Config Pipeline End-to-End

**Files:** `src/DevForge.Daemon/Config/ConfigPipeline.cs`, test file

Wire together: SiteConfigLoader (TOML) -> TemplateEngine (Scriban) -> ConfigValidator (httpd -t) -> AtomicWriter (temp + rename). Archive history. Reference: SPEC.md Section 8 pipeline diagram.

**Commit:** `git commit -m "feat: config pipeline TOML to validated atomic write"`

---

### Task 3.3: Implement HostsFileManager

**Files:** `src/DevForge.Daemon/Dns/HostsFileManager.cs`, test file

Manage `# >>> DevForge Managed <<<` block in hosts file. Add/remove `127.0.0.1 domain` entries. Never touch content outside managed block. Cross-platform paths. Reference: SPEC.md Section 12.

**Commit:** `git commit -m "feat: hosts file manager with managed block"`

---

### Task 3.4: UAC Elevation Helper (Windows)

**Files:** `src/DevForge.Daemon/Dns/ElevationHelper.cs`, test file

Use `ProcessStartInfo { Verb = "runas" }` for hosts file writes on Windows. Validate payload (only managed block operations). Reference: SPEC.md Section 12.

**Commit:** `git commit -m "feat: UAC elevation helper for hosts file writes"`

---

### Task 3.5: Implement MkcertManager

**Files:** `src/DevForge.Daemon/Ssl/MkcertManager.cs`, test file

Wrap mkcert binary via CliWrap. CA install (`mkcert -install`), per-site cert generation. Store at `~/.devforge/ssl/sites/{domain}/`. Track in SQLite certificates table. Reference: SPEC.md Section 13.

**Commit:** `git commit -m "feat: mkcert manager with CA install and cert generation"`

---

### Task 3.6: Framework Auto-Detection

**Files:** `src/DevForge.Core/Configuration/FrameworkDetector.cs`, test file

Detect Nette (composer.json contains nette/application), Laravel (artisan file), WordPress (wp-config.php), Symfony. Set document root accordingly. Reference: SPEC.md Section 11.

**Code:**
```csharp
// src/DevForge.Core/Configuration/FrameworkDetector.cs
using DevForge.Core.Models;

namespace DevForge.Core.Configuration;

public static class FrameworkDetector
{
    public static Framework Detect(string projectRoot)
    {
        if (File.Exists(Path.Combine(projectRoot, "artisan")))
            return Framework.Laravel;

        if (File.Exists(Path.Combine(projectRoot, "wp-config.php")) ||
            File.Exists(Path.Combine(projectRoot, "wp-config-sample.php")))
            return Framework.WordPress;

        var composerPath = Path.Combine(projectRoot, "composer.json");
        if (File.Exists(composerPath))
        {
            var content = File.ReadAllText(composerPath);
            if (content.Contains("nette/application")) return Framework.Nette;
            if (content.Contains("symfony/framework-bundle")) return Framework.Symfony;
        }

        return Framework.Generic;
    }

    public static string GetDocumentRoot(string projectRoot, Framework framework) => framework switch
    {
        Framework.Nette => Path.Combine(projectRoot, "www"),
        Framework.Laravel => Path.Combine(projectRoot, "public"),
        Framework.Symfony => Path.Combine(projectRoot, "public"),
        Framework.WordPress => projectRoot,
        _ => projectRoot
    };
}
```

**Test:**
```csharp
// tests/DevForge.Core.Tests/Configuration/FrameworkDetectorTests.cs
using DevForge.Core.Configuration;
using DevForge.Core.Models;

namespace DevForge.Core.Tests.Configuration;

public class FrameworkDetectorTests
{
    [Fact]
    public void Detect_Nette_By_Composer()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "composer.json"),
            """{"require":{"nette/application":"^3.0"}}""");
        try
        {
            Assert.Equal(Framework.Nette, FrameworkDetector.Detect(dir));
            Assert.EndsWith("www", FrameworkDetector.GetDocumentRoot(dir, Framework.Nette));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detect_Laravel_By_Artisan()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "artisan"), "#!/usr/bin/env php");
        try
        {
            Assert.Equal(Framework.Laravel, FrameworkDetector.Detect(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detect_Generic_For_Empty_Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(Framework.Generic, FrameworkDetector.Detect(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

**Commit:** `git commit -m "feat: framework auto-detection for Nette, Laravel, WordPress"`

---

### Task 3.7: Domain Validation

**Files:** `src/DevForge.Core/Configuration/DomainValidator.cs`, test file

Strict regex per SPEC.md Section 11. Reject: whitespace, null bytes, path traversal, shell metacharacters, quotes, angle brackets.

**Code:**
```csharp
// src/DevForge.Core/Configuration/DomainValidator.cs
using System.Text.RegularExpressions;

namespace DevForge.Core.Configuration;

public static partial class DomainValidator
{
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex DomainRegex();

    private static readonly char[] ForbiddenChars = [' ', '\t', '\n', '\r', '\0', ';', '|', '&', '$', '`', '>', '<', '\'', '"'];

    public static Core.Models.ValidationResult Validate(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return Core.Models.ValidationResult.Failure("Domain cannot be empty");

        if (domain.Contains("../") || domain.Contains("..\\"))
            return Core.Models.ValidationResult.Failure("Domain contains path traversal");

        if (domain.Any(c => ForbiddenChars.Contains(c)))
            return Core.Models.ValidationResult.Failure("Domain contains forbidden characters");

        if (!DomainRegex().IsMatch(domain))
            return Core.Models.ValidationResult.Failure("Domain does not match RFC 952 format");

        return Core.Models.ValidationResult.Success();
    }
}
```

**Commit:** `git commit -m "feat: domain validation with injection prevention"`

---

### Task 3.8: CLI Site and SSL Commands

**Files:** `src/DevForge.Cli/Commands/SiteCommand.cs`, `SslCommand.cs`, `DnsCommand.cs`

Implement: `devforge new <domain>` with flags (--php, --ssl, --db, --nette), `site:list`, `site:delete`, `ssl:create`, `dns:flush`. Wire to gRPC RPCs. Reference: SPEC.md Section 15.

**Commit:** `git commit -m "feat: CLI site, ssl, and dns commands"`

---

### Task 3.9: Integration Test -- Full Site Creation

**Files:** `tests/DevForge.Daemon.Tests/Integration/SiteCreationTests.cs`

End-to-end test: create site via gRPC -> verify TOML file exists -> verify generated Apache config -> verify hosts entry -> verify SQLite row.

**Commit:** `git commit -m "test: end-to-end site creation integration test"`

---

## Phase 4: GUI + Logging

**Purpose:** Build all GUI screens with real-time gRPC streaming. (SPEC.md Section 16; avalonia-ui-patterns.md)

---

### Task 4.1: Dashboard Screen with ServiceCards

**Files:** `src/DevForge.Gui/Views/DashboardPage.axaml`, `ViewModels/DashboardViewModel.cs`, `Controls/ServiceCard.axaml`

Implement per avalonia-ui-patterns.md Sections 3 and 5. Service cards with name, state dot, CPU/RAM, Start/Stop/Restart buttons.

**Commit:** `git commit -m "feat: dashboard screen with service cards"`

---

### Task 4.2: LiveCharts2 Real-Time CPU/RAM Charts

**Files:** modify `DashboardViewModel.cs`, `DashboardPage.axaml`

Add CartesianChart with LineSeries for CPU % over 60-second window. 30 FPS throttle per avalonia-ui-patterns.md Section 11.

**Commit:** `git commit -m "feat: livecharts2 real-time CPU/RAM sparklines"`

---

### Task 4.3: Sites Manager Screen

**Files:** `src/DevForge.Gui/Views/SitesPage.axaml`, `ViewModels/SitesViewModel.cs`, `Controls/SiteCard.axaml`

Per avalonia-ui-patterns.md Sections 4 and 6. Search/filter, WrapLayout card grid, empty state, SiteCard with domain/SSL/PHP badge.

**Commit:** `git commit -m "feat: sites manager screen with card layout"`

---

### Task 4.4: Create Site Wizard

**Files:** `src/DevForge.Gui/Views/CreateSiteDialog.axaml`, `ViewModels/CreateSiteWizardViewModel.cs`

4-step wizard per avalonia-ui-patterns.md Section 7. Domain + docroot -> PHP version -> SSL/webserver -> database. Carousel or TransitioningContentControl.

**Commit:** `git commit -m "feat: multi-step create site wizard"`

---

### Task 4.5: PHP Manager Screen

**Files:** `src/DevForge.Gui/Views/PhpManagerPage.axaml`, `ViewModels/PhpManagerViewModel.cs`

List installed PHP versions, default badge, extensions count, site count. Set Default button.

**Commit:** `git commit -m "feat: PHP version manager screen"`

---

### Task 4.6: SSL Manager Screen

**Files:** `src/DevForge.Gui/Views/SslManagerPage.axaml`, `ViewModels/SslManagerViewModel.cs`

CA status, cert list with expiry dates, Generate Certificate button, expiry warnings.

**Commit:** `git commit -m "feat: SSL manager screen with cert status"`

---

### Task 4.7: Log Viewer with gRPC StreamLogs

**Files:** `src/DevForge.Gui/Views/LogViewerPage.axaml`, `ViewModels/LogViewerViewModel.cs`

Service selector dropdown, gRPC `StreamLogs` subscription, auto-scroll with pause-on-select, level filter (info/warn/error).

**Commit:** `git commit -m "feat: log viewer with gRPC streaming"`

---

### Task 4.8: System Tray with NativeMenu

**Files:** `src/DevForge.Gui/App.axaml` (modify), `ViewModels/AppViewModel.cs` (create)

TrayIcon with context menu per avalonia-ui-patterns.md Section 1. Service status dots, Start/Stop All, Recent Sites, Quit.

**Commit:** `git commit -m "feat: system tray with service status and quick actions"`

---

### Task 4.9: gRPC StreamMetrics UI Thread Throttling

**Files:** modify `DashboardViewModel.cs`

Implement 30 FPS throttle per avalonia-ui-patterns.md Section 11. `Dispatcher.UIThread.InvokeAsync` with `DispatcherPriority.Render`. `CancellationTokenSource` for cleanup.

**Commit:** `git commit -m "feat: gRPC metrics streaming with 30fps UI throttle"`

---

### Task 4.10: Theme Toggle (Dark/Light/System)

**Files:** modify `App.axaml.cs`, `ViewModels/SettingsViewModel.cs`

Per avalonia-ui-patterns.md Sections 8 and 1. ComboBox with Dark/Light/System, `App.ApplyTheme()`, LiveCharts theme sync.

**Commit:** `git commit -m "feat: dark/light/system theme toggle"`

---

## Phase 5: CLI + Polish

**Purpose:** Complete CLI command coverage, shell completions, database management. (SPEC.md Sections 14, 15)

---

### Task 5.1: All Remaining CLI Commands

**Files:** `src/DevForge.Cli/Commands/PhpCommand.cs`, `DbCommand.cs`, `ConfigCommand.cs`, `DaemonCommand.cs`

Implement: `php:list`, `php:default`, `db:list`, `db:create`, `db:drop`, `db:import`, `db:export`, `config:get`, `config:set`, `config:rebuild`, `daemon start/stop/restart`.

**Commit:** `git commit -m "feat: complete CLI command tree"`

---

### Task 5.2: Shell Completions

**Files:** modify `src/DevForge.Cli/Program.cs`

Enable System.CommandLine built-in completions. `devforge completion bash|zsh|fish|powershell` output.

**Commit:** `git commit -m "feat: shell completions for bash, zsh, fish, powershell"`

---

### Task 5.3: --json Output for All Commands

**Files:** modify all command files

Ensure every command respects `--json` global option. Error format: `{"error": true, "code": "...", "message": "..."}`. Success: `{"success": true, "data": {...}}`.

**Commit:** `git commit -m "feat: json output mode for all CLI commands"`

---

### Task 5.4: Database Manager

**Files:** `src/DevForge.Daemon/Db/DatabaseManager.cs`, test file

Create/drop/import/export MySQL databases via CliWrap. Stream import/export for large files. Reference: SPEC.md Section 14.

**Commit:** `git commit -m "feat: database manager with import/export streaming"`

---

### Task 5.5: Settings Screen in GUI

**Files:** `src/DevForge.Gui/Views/SettingsPage.axaml`, `ViewModels/SettingsViewModel.cs`

Per avalonia-ui-patterns.md Section 8. Port config, DNS settings, default PHP, theme, startup options.

**Commit:** `git commit -m "feat: settings screen with port and theme config"`

---

### Task 5.6: Keyboard Shortcuts

**Files:** modify `MainWindow.axaml`, `MainWindowViewModel.cs`

Ctrl+K command palette, Ctrl+N new site, Ctrl+1-7 sidebar sections, F5 refresh, Space toggle service, Esc close modal. Reference: SPEC.md Section 16.

**Commit:** `git commit -m "feat: keyboard shortcuts for navigation and actions"`

---

## Phase 6: Plugins + Packaging

**Purpose:** Plugin system, optional service plugins, installer, auto-updater. (SPEC.md Sections 17, 21)

---

### Task 6.1: Plugin System (AssemblyLoadContext)

**Files:** `src/DevForge.Daemon/Plugin/PluginLoader.cs`, `PluginLoadContext.cs`, test file

Implement `PluginLoader` per SPEC.md Section 17. Load `IServiceModule` implementations from `~/.devforge/plugins/{id}/`. Parse `plugin.json` manifest. `AssemblyLoadContext.Unload()` for hot-unload.

**Code:**
```csharp
// src/DevForge.Daemon/Plugin/PluginLoadContext.cs
using System.Reflection;
using System.Runtime.Loader;

namespace DevForge.Daemon.Plugin;

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}
```

**Commit:** `git commit -m "feat: plugin system with AssemblyLoadContext isolation"`

---

### Task 6.2: Refactor Built-in Services as Plugins

**Files:** modify `ApacheModule.cs`, `MySqlModule.cs`, `PhpFpmModule.cs`

Ensure all built-in service modules implement `IServiceModule` cleanly. Register via `ProcessManager.RegisterModule()` at startup. Add `is_builtin = 1` flag in SQLite plugins table.

**Commit:** `git commit -m "refactor: built-in services as proper IServiceModule plugins"`

---

### Task 6.3: Redis Plugin

**Files:** `src/DevForge.Plugin.Redis/RedisModule.cs`, `plugin.json`

Implement `IServiceModule` for Redis 7.x. Start `redis-server`, stop via `redis-cli shutdown`, health check TCP 6379.

**Commit:** `git commit -m "feat: redis service plugin"`

---

### Task 6.4: Mailpit Plugin

**Files:** `src/DevForge.Plugin.Mailpit/MailpitModule.cs`, `plugin.json`

Implement `IServiceModule` for Mailpit. SMTP port 1025, UI port 8025. Health check HTTP `/api/v1/info`.

**Commit:** `git commit -m "feat: mailpit email testing plugin"`

---

### Task 6.5: WiX MSI Installer

**Files:** `installer/DevForge.wxs`, `installer/build-msi.ps1`

WiX installer for Windows. Install to `C:\Program Files\DevForge\`. Add to PATH. Register elevation helper scheduled task. Reference: SPEC.md Section 21.

**Commit:** `git commit -m "feat: WiX MSI installer for Windows"`

---

### Task 6.6: Velopack Auto-Updater

**Files:** `src/DevForge.Daemon/Updates/UpdateChecker.cs`

Check for updates every 24 hours. Download delta updates. Support stable/beta channels. Reference: SPEC.md Section 21.

**Commit:** `git commit -m "feat: Velopack auto-updater with delta support"`

---

### Task 6.7: MAMP PRO Migration Tool

**Files:** `src/DevForge.Cli/Commands/MigrateCommand.cs`, `src/DevForge.Daemon/Migration/MampImporter.cs`

Read MAMP PRO SQLite database, extract site configs, convert to DevForge TOML format. `devforge migrate:mamp-pro <mamp-db-path>`. Reference: SPEC.md Section 1 (pain points).

**Commit:** `git commit -m "feat: MAMP PRO migration import tool"`

---

## Summary

| Phase | Tasks | Focus |
|-------|-------|-------|
| Phase 0 | 5 | Day-1 verification of all dependencies |
| Phase 1 | 14 | Solution, models, proto, SQLite, daemon, GUI shell, CLI |
| Phase 2 | 12 | Process management, state machine, Apache/MySQL/PHP modules, health monitoring |
| Phase 3 | 9 | Sites CRUD, config pipeline, hosts file, SSL, framework detection |
| Phase 4 | 10 | All GUI screens, LiveCharts2, tray icon, gRPC streaming |
| Phase 5 | 6 | CLI completeness, shell completions, database manager, settings |
| Phase 6 | 7 | Plugin system, Redis/Mailpit, installer, auto-updater, MAMP import |
| **Total** | **63** | |

**Estimated time:** 12 weeks for a solo developer. Each task is 2-15 minutes for a skilled C# developer with Claude Code assistance.

**Key files** (most important to get right):
- `src/DevForge.Core/Proto/devforge.proto` -- API contract for entire system
- `src/DevForge.Daemon/Config/ConfigPipeline.cs` -- eliminates MAMP config corruption
- `src/DevForge.Daemon/Services/ServiceUnit.cs` -- state machine for all service lifecycle
- `src/DevForge.Core/Configuration/SiteConfig.cs` -- TOML source of truth model
- `src/DevForge.Daemon/Services/ProcessManager.cs` -- coordinates all service modules

---

I was unable to write the file to disk because I do not have a Write, Edit, or Bash tool available in this session. The complete content above (approximately 3,500 lines of markdown) should be written to `C:\work\sources\nks-ws\docs\superpowers\plans\2026-04-09-devforge-implementation.md`. The directory `docs\superpowers\plans\` needs to be created first.

Key source files referenced in this plan:
- `C:\work\sources\nks-ws\SPEC.md` -- authoritative specification (1,984 lines, all 23 sections)
- `C:\work\sources\nks-ws\docs\plans\csharp-process-management.md` -- process management patterns, Job Objects, CliWrap, health checks
- `C:\work\sources\nks-ws\docs\plans\avalonia-ui-patterns.md` -- complete AXAML and C# code for all UI components
- `C:\work\sources\nks-ws\prototype\database\migrations\001_initial.sql` -- canonical SQLite DDL
- `C:\work\sources\nks-ws\prototype\database\triggers.sql` -- audit trail triggers
- `C:\work\sources\nks-ws\prototype\database\views.sql` -- dashboard views
- `C:\work\sources\nks-ws\prototype\database\indexes.sql` -- performance indexes
- `C:\work\sources\nks-ws\prototype\database\seed.sql` -- default data