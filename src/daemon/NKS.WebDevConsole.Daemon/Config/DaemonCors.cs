namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>Origins allowed to read daemon responses cross-origin.</summary>
public static class DaemonCors
{
    /// <summary>
    /// Ports of the electron-vite renderer dev server: 5190 in
    /// src/frontend/electron.vite.config.ts, with strictPort: false so Vite
    /// moves to the next free port when 5190 is taken.
    /// </summary>
    public const int DevServerPortFirst = 5190;
    public const int DevServerPortLast = 5199;

    public static bool IsAllowedOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        // Packaged Electron renderer (protocol.handle('app') in electron/main.ts).
        if (uri.Scheme == "app" && uri.Host == "nks-wdc") return true;
        // electron-vite dev server on this machine. Any other loopback origin
        // (another local dev server, a page served by a site WDC manages) gets
        // no CORS grant.
        return uri.Scheme == Uri.UriSchemeHttp
               && uri.IsLoopback
               && uri.Port is >= DevServerPortFirst and <= DevServerPortLast;
    }

    /// <summary>
    /// Answers an unhandled exception with a JSON error. Kestrel's own 500 for
    /// it skips the response OnStarting callbacks, CORS headers included, so
    /// the renderer reported a CORS failure instead of the error. Register
    /// right after <c>UseCors()</c>.
    /// </summary>
    public static IApplicationBuilder UseErrorsWithCors(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        try
        {
            await next();
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled")
                .LogError(ex, "Unhandled error on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            ctx.Response.StatusCode = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "internal_error", detail = ex.Message });
        }
    });
}
