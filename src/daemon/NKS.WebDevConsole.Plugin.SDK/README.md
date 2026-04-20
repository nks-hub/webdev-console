# NKS.WebDevConsole.Plugin.SDK

Base library for building plugins that extend [NKS WebDev Console](https://github.com/nks-hub/webdev-console).

## Install

```xml
<PackageReference Include="NKS.WebDevConsole.Plugin.SDK" Version="0.2.*" />
```

The `.nupkg` is attached to each GitHub Release of the monorepo (under
`github.com/nks-hub/webdev-console/releases`). A GitHub Packages feed is *not*
used — fetching is via release asset download to avoid anonymous 403s on
cross-org package reads. Plugin CI pipelines in
[webdev-console-plugins](https://github.com/nks-hub/webdev-console-plugins)
download the nupkg into a local feed before `dotnet restore`. See that repo's
`CONTRIBUTING.md` for the `NUGET_GH_*` env contract used by local dev.

## Writing a plugin

Implement `IWdcPlugin` (or derive from `PluginBase`):

```csharp
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK;

public sealed class MyPlugin : PluginBase
{
    public override string Id => "nks.wdc.myplugin";
    public override string DisplayName => "My Plugin";
    public override string Version => "0.1.0";

    public override void Initialize(IServiceCollection services, IPluginContext ctx)
    {
        ctx.Endpoints.MapGet("/ping", () => Results.Ok(new { ok = true }));
    }
}
```

Ship a `plugin.json` next to your DLL (see
[`docs/MANIFEST.md`](https://github.com/nks-hub/webdev-console-plugins/blob/main/docs/MANIFEST.md)
for the full schema).

Full reference + walkthrough:
[`docs/PLUGIN-API.md`](https://github.com/nks-hub/webdev-console-plugins/blob/main/docs/PLUGIN-API.md).

## License

MIT.
