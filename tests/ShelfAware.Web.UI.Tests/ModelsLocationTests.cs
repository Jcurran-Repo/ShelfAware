using System.Text.RegularExpressions;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Where a droplet keeps its voice and ear models, held to ONE definition: the deploy bootstrap's
/// <c>MODELS=</c> (<see cref="PastedInstalls.ModelsRoot"/>).
///
/// <para>⚠️ The path is written about thirty times — every pasted install, every documented
/// <c>Speech__*__ModelDirectory</c> line, both env examples — because each has to be complete enough to
/// paste. It moved once, out of the app's home, and a copy left behind at the old path is not a typo:
/// a box configured from it loads its model from a directory the app owns and can replace. So the copies
/// are held to the bootstrap here, rather than by a sentence promising they agree.</para>
/// </summary>
public class ModelsLocationTests
{
    /// <summary>The service account's home — its DataDir (deploy-droplet.md: adduser --home).</summary>
    private const string AppHome = "/var/lib/shelfaware";

    /// <summary>The old location, which only the switch-over instructions may still name.</summary>
    private const string OldModels = "/var/lib/shelfaware/models";

    [Fact]
    public void The_models_live_outside_the_apps_home()
    {
        var root = PastedInstalls.ModelsRoot();

        // Anything directly inside a directory the app owns, the app can rename away and replace — and it
        // loads its voice by that name. Root-owned /var/lib is what stops that.
        Assert.StartsWith("/", root);
        Assert.False(root == AppHome || root.StartsWith(AppHome + "/", StringComparison.Ordinal),
            $"The bootstrap's MODELS={root} is inside the service account's home ({AppHome}).");
    }

    [Theory]
    [InlineData("deploy-kokoro.md")]
    [InlineData("deploy-moonshine.md")]
    [InlineData("deploy-piper.md")]
    [InlineData("voice-bakeoff.md")]
    public void Every_pasted_install_goes_where_the_bootstrap_does(string doc)
    {
        var root = PastedInstalls.ModelsRoot();
        var bash = PastedInstalls.In(File.ReadAllText(RepoTree.FileAt(Path.Combine("docs", doc))))
            .Where(b => b.IsBash).ToList();

        Assert.NotEmpty(bash);
        var elsewhere = bash.Where(b => b.ModelsRoot != root).Select(b => $"{b.Archive} → {b.ModelsRoot}").ToList();
        Assert.True(elsewhere.Count == 0,
            $"docs/{doc} installs somewhere other than the bootstrap's MODELS={root}:" + Environment.NewLine
            + string.Join(Environment.NewLine, elsewhere));
    }

    /// <summary>Every Linux <c>Speech__*__ModelDirectory=</c> the docs and env examples show a box is under the
    /// bootstrap's directory — so a box configured by copying one loads what the bootstrap installed.</summary>
    [Fact]
    public void Every_documented_model_directory_is_under_the_bootstrap_root()
    {
        var root = PastedInstalls.ModelsRoot();
        var files = DocsAndEnvExamples();
        var settings = files
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"Speech__\w+__ModelDirectory=(/\S*)")
                .Select(m => (File: Path.GetFileName(f), Path: m.Groups[1].Value)))
            .ToList();

        Assert.True(settings.Count >= 10,
            $"Only {settings.Count} Linux Speech__*__ModelDirectory= line(s) found — the scan is broken, not the docs.");
        var outside = settings.Where(s => !s.Path.StartsWith(root + "/", StringComparison.Ordinal))
            .Select(s => $"{s.File}: {s.Path}").ToList();
        Assert.True(outside.Count == 0,
            $"A documented model directory is not under the bootstrap's MODELS={root}:" + Environment.NewLine
            + string.Join(Environment.NewLine, outside));
    }

    /// <summary>The old location survives only where it has to: the switch-over steps for a box that still
    /// has models there, and the bootstrap's warning that says so.</summary>
    [Fact]
    public void Only_the_switch_over_names_the_old_location()
    {
        string[] allowed = ["deploy-droplet.md", "deploy-droplet.yml"];
        var mentions = EverythingABoxIsBuiltFrom()
            .Where(f => File.ReadAllText(f).Contains(OldModels, StringComparison.Ordinal))
            .ToList();

        // The switch-over itself must still be written down — and only as prose: inside it, the old path
        // may never be something to install into or run against.
        var switchOver = Assert.Single(mentions, f => Path.GetFileName(f) == "deploy-droplet.md");
        var runnable = File.ReadAllLines(switchOver)
            .Where(l => l.Contains(OldModels, StringComparison.Ordinal)
                        && (l.Contains("M=", StringComparison.Ordinal) || l.Contains("VoiceCheck", StringComparison.Ordinal)
                            || l.Contains("ModelDirectory=", StringComparison.Ordinal)))
            .ToList();
        Assert.True(runnable.Count == 0,
            "deploy-droplet.md uses the old models location as something to install into or run against:"
            + Environment.NewLine + string.Join(Environment.NewLine, runnable));

        var stray = mentions.Select(Path.GetFileName).Where(f => !allowed.Contains(f)).ToList();
        Assert.True(stray.Count == 0, $"{OldModels} is still named in: {string.Join(", ", stray)}");
    }

    /// <summary>Everything a box is configured from or built from: the docs and env examples, the README,
    /// every file under deploy/, the workflows, and the source of the app and its tools.</summary>
    private static IEnumerable<string> EverythingABoxIsBuiltFrom()
    {
        var root = RepoTree.Root().FullName;
        return DocsAndEnvExamples()
            .Append(Path.Combine(root, "README.md"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml"))
            .Concat(new[] { "src", "tools" }
                .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && Path.GetExtension(f) is ".cs" or ".razor" or ".json" or ".sh" or ".ps1" or ".md"))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>The docs a person configures a box from, and the env examples — not the journal, which is
    /// history and is allowed to describe where things used to be.</summary>
    private static IEnumerable<string> DocsAndEnvExamples()
    {
        var root = RepoTree.Root().FullName;
        return Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs", "features"), "*.md"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*.example"));
    }
}
