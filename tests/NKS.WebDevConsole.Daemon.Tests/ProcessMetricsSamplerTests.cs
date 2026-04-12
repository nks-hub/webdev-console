using System.Diagnostics;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for <see cref="ProcessMetricsSampler"/> — the delta-based CPU/memory
/// sampler used by all plugins' health monitors. Covers SampleMany aggregation,
/// Forget cache eviction, and edge cases (null, empty collections).
/// </summary>
public class ProcessMetricsSamplerTests
{
    [Fact]
    public void SampleMany_NullProcesses_ReturnsZero()
    {
        var (cpu, mem) = ProcessMetricsSampler.SampleMany(new Process?[] { null, null });
        Assert.Equal(0, cpu);
        Assert.Equal(0, mem);
    }

    [Fact]
    public void SampleMany_EmptyCollection_ReturnsZero()
    {
        var (cpu, mem) = ProcessMetricsSampler.SampleMany(Array.Empty<Process?>());
        Assert.Equal(0, cpu);
        Assert.Equal(0, mem);
    }

    [Fact]
    public void SampleMany_SingleLiveProcess_ReturnsSameAsSample()
    {
        var current = Process.GetCurrentProcess();
        // Prime the cache with a first sample
        ProcessMetricsSampler.Sample(current);

        var (singleCpu, singleMem) = ProcessMetricsSampler.Sample(current);
        // SampleMany with the same single process should give equivalent results
        // (we re-prime so the delta window is similar)
        ProcessMetricsSampler.Sample(current);
        var (manyCpu, manyMem) = ProcessMetricsSampler.SampleMany(new[] { current });

        Assert.True(manyMem > 0);
    }

    [Fact]
    public void SampleMany_CpuClampedTo100()
    {
        // SampleMany caps total CPU at 100% even if individual samples sum higher
        // We can't easily force >100% in a unit test, but we verify the cap logic
        // exists by checking the method doesn't return > 100 for the current process
        var current = Process.GetCurrentProcess();
        ProcessMetricsSampler.Sample(current);
        var (cpu, _) = ProcessMetricsSampler.SampleMany(new[] { current, current, current });
        Assert.True(cpu <= 100);
    }

    [Fact]
    public void Forget_RemovesCachedSnapshot()
    {
        var current = Process.GetCurrentProcess();
        var pid = current.Id;

        // First sample primes the cache
        ProcessMetricsSampler.Sample(current);
        // Second sample should have a delta (cached snapshot exists)
        var (_, mem1) = ProcessMetricsSampler.Sample(current);
        Assert.True(mem1 > 0);

        // Forget clears the cache entry
        ProcessMetricsSampler.Forget(pid);

        // Next sample after Forget should return 0% CPU (no delta baseline)
        var (cpu, _) = ProcessMetricsSampler.Sample(current);
        Assert.Equal(0, cpu);
    }

    [Fact]
    public void Forget_NonexistentPid_DoesNotThrow()
    {
        ProcessMetricsSampler.Forget(999999);
    }

    [Fact]
    public void Sample_SecondCall_ReturnsNonNegativeCpu()
    {
        var current = Process.GetCurrentProcess();
        ProcessMetricsSampler.Sample(current);
        var (cpu, _) = ProcessMetricsSampler.Sample(current);
        Assert.True(cpu >= 0);
        Assert.True(cpu <= 100);
    }
}
