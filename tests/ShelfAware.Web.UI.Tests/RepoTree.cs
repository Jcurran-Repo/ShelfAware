namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Where the repository is, for the rules in this project that read files the build does not copy —
/// Razor sources, docs, workflows. Found by walking up from the test assembly to the solution file, so
/// no rule depends on the build's output layout.
///
/// <para>⚠️ ONE copy. Every rule that reads the tree needs this walk, and each one that wrote its own
/// would be one more place for the "no solution found" case to be handled differently — or not at all,
/// which is how a scan ends up reading nothing and passing.</para>
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
}
