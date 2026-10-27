using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

public class ServiceLogTailerTests
{
    [Fact]
    public void NewLines_ReturnsOnlyLinesAfterTheOverlap()
    {
        var prev = new[] { "a", "b", "c" };
        var next = new[] { "b", "c", "d", "e" };
        Assert.Equal(new[] { "d", "e" }, ServiceLogTailer.NewLines(prev, next));
    }

    [Fact]
    public void NewLines_UnchangedWindow_ReturnsNothing()
    {
        var w = new[] { "x", "y" };
        Assert.Empty(ServiceLogTailer.NewLines(w, w));
    }

    [Fact]
    public void NewLines_RepeatedLines_UseLongestOverlap()
    {
        var prev = new[] { "ok", "ok", "ok" };
        var next = new[] { "ok", "ok", "ok", "err" };
        Assert.Equal(new[] { "err" }, ServiceLogTailer.NewLines(prev, next));
    }

    [Fact]
    public void NewLines_NoOverlap_TreatsWholeWindowAsNew()
    {
        var prev = new[] { "old1", "old2" };
        var next = new[] { "new1", "new2" };
        Assert.Equal(next, ServiceLogTailer.NewLines(prev, next));
    }
}
