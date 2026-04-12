namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Ascending comparer that sorts version-like strings numerically per
/// dot-separated segment so <c>"20.5.0"</c> ranks above <c>"9.0.0"</c>.
/// Non-numeric segments fall back to ordinal comparison, and a pre-release
/// suffix (<c>-beta.1</c>) sorts below the stable release.
///
/// Use via <c>OrderByDescending(x, SemverVersionComparer.Instance)</c> to
/// pick the highest installed version directory under
/// <c>~/.wdc/binaries/{tool}/</c>. A plain <c>StringComparer.Ordinal</c>
/// ranks "9.0.0" above "20.5.0" because ASCII <c>'9' &gt; '2'</c> — wrong
/// as soon as any managed tool reaches a 2-digit major version alongside
/// a lingering 1-digit install.
///
/// This type lives in Core.Services so all plugins loaded in isolated
/// AssemblyLoadContexts can resolve the same comparer without duplicating
/// the implementation (Core is in <c>PluginLoadContext.SharedAssemblies</c>,
/// so the type is identity-stable across ALCs).
/// </summary>
public sealed class SemverVersionComparer : IComparer<string>
{
    public static readonly SemverVersionComparer Instance = new();

    private SemverVersionComparer() { }

    public int Compare(string? x, string? y) => CompareAscending(x, y);

    /// <summary>
    /// Core comparison helper. Public so tests can exercise the pairwise
    /// logic without routing through an ordered sequence.
    /// </summary>
    public static int CompareAscending(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        // Split pre-release suffix (everything after the first '-')
        var aDash = a.IndexOf('-');
        var bDash = b.IndexOf('-');
        var aMain = aDash >= 0 ? a[..aDash] : a;
        var bMain = bDash >= 0 ? b[..bDash] : b;
        var aPre = aDash >= 0 ? a[(aDash + 1)..] : "";
        var bPre = bDash >= 0 ? b[(bDash + 1)..] : "";

        // Compare main segments numerically where possible
        var aSegs = aMain.Split('.');
        var bSegs = bMain.Split('.');
        var len = Math.Max(aSegs.Length, bSegs.Length);
        for (int i = 0; i < len; i++)
        {
            var aSeg = i < aSegs.Length ? aSegs[i] : "0";
            var bSeg = i < bSegs.Length ? bSegs[i] : "0";
            if (int.TryParse(aSeg, out var aNum) && int.TryParse(bSeg, out var bNum))
            {
                var cmp = aNum.CompareTo(bNum);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = string.CompareOrdinal(aSeg, bSeg);
                if (cmp != 0) return cmp;
            }
        }

        // Per semver: a version WITHOUT a pre-release suffix ranks ABOVE
        // one with the same main segments but a pre-release suffix.
        //   1.0.0       > 1.0.0-rc.1
        //   1.0.0-rc.2  > 1.0.0-rc.1
        if (aPre.Length == 0 && bPre.Length == 0) return 0;
        if (aPre.Length == 0) return 1;
        if (bPre.Length == 0) return -1;
        return string.CompareOrdinal(aPre, bPre);
    }
}
