namespace ShelfAware.TestSupport;

/// <summary>
/// Where the repository is, for the rules that read files the build does not copy — Razor sources, docs,
/// workflows, the deploy bootstrap. Found by walking up from the test assembly to the solution file, so no
/// rule depends on the build's output layout.
///
/// <para>⚠️ ONE copy, and it is linked into both rule-bearing test projects as SOURCE (see the
/// <c>&lt;Compile Include="..\Shared\RepoTree.cs"&gt;</c> item in each <c>.csproj</c>) rather than being
/// referenced, because the two assemblies have no dependency path between them and inventing one so a
/// Core-only suite could see a Blazor suite would be the wrong shape entirely.</para>
///
/// <para>⚠️ This file exists because there were FOUR copies of this four-line walk — two of them written
/// in the same week, in two pull requests that each named folding them together as the other's follow-up.
/// And they were not even the same four lines: two wrapped the walk in a file lookup that asserts the
/// file is there, and two handed back the root alone and left every caller to combine a path and hope. So
/// whether a rule was protected from silently scanning a file that had moved came down to which copy its
/// author happened to start from — which is the failure CLAUDE.md names as this repo's most expensive, in
/// its cheapest possible form. <see cref="DirectoryAt"/> is the same guard for the callers that wanted a
/// directory and had nothing. The rule is now held by <c>RepoRootWalkRulesTests</c>, not by this
/// paragraph.</para>
/// </summary>
internal static class RepoTree
{
    /// <summary>The repository root: the first directory above the test assembly holding the solution.</summary>
    internal static DirectoryInfo Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShelfAware.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir); // no solution above the test assembly — the walk is wrong, not the sources
        return dir!;
    }

    /// <summary>A file inside the repository, asserted to exist — a rule reading a file that moved would
    /// otherwise pass having read nothing.</summary>
    internal static string FileAt(string relativePath)
    {
        var file = Path.Combine(Root().FullName, relativePath);
        Assert.True(File.Exists(file), $"{relativePath} is not where this test expects it.");
        return file;
    }

    /// <summary>A directory inside the repository, asserted to exist — same reason as
    /// <see cref="FileAt"/>: a scan pointed at a directory that moved enumerates nothing and passes.</summary>
    internal static string DirectoryAt(string relativePath)
    {
        var dir = Path.Combine(Root().FullName, relativePath);
        Assert.True(Directory.Exists(dir), $"{relativePath} is not where this test expects it.");
        return dir;
    }
}
