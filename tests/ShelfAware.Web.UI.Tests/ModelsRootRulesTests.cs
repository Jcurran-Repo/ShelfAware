using System.Text.RegularExpressions;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Where a Linux box keeps its models is written down in three kinds of place — the deploy's bootstrap,
/// the <c>M=</c> line of every pasteable install, and every <c>Speech__*__ModelDirectory</c> in the docs
/// and the two env examples — and this is what stops them drifting apart. (No total here on purpose:
/// the counts that have to be true are the reach guards below, which fail rather than go stale.)
///
/// <para>⚠️ The location is a SECURITY property, not a preference. The models moved out of
/// <c>/var/lib/shelfaware</c> — the service account's own home, where a compromised app could rename
/// <c>models/</c> between deploys and be served its own files under that name — into a root-owned
/// parent the app cannot write to. A single site left behind does not fail loudly: it installs a model
/// somewhere real, or points a box at a directory that is exactly as writable as the old one, and
/// everything boots and speaks. So the one site that disagrees is the one nobody notices.</para>
///
/// <para>Duplicated deliberately, for the same reason the hashes are: an operator pastes these blocks as
/// root, and telling them to go and read a YAML file for the path is how a box ends up half-moved. The
/// honest fix for a fact that must be written many times is a rule that fails the build when two of them
/// disagree.</para>
/// </summary>
public class ModelsRootRulesTests
{
    /// <summary>Every file that names the models root for a Linux box. The env examples are here because
    /// they are what a rebuilt droplet is configured from — <c>deploy/env.example</c> was already found
    /// stale once, and a rebuilt box would have been given the old voice back.</summary>
    private static readonly string[] Sites =
    [
        Path.Combine("docs", "deploy-kokoro.md"),
        Path.Combine("docs", "deploy-moonshine.md"),
        Path.Combine("docs", "deploy-piper.md"),
        Path.Combine("docs", "voice-bakeoff.md"),
        Path.Combine("docs", "deploy-droplet.md"),
        Path.Combine("deploy", "env.example"),
        Path.Combine("deploy", "demo-box.env.example"),
    ];

    /// <summary>The one path the migration section of <c>deploy-droplet.md</c> is allowed to name: the old
    /// location, because the steps for moving a running box off it have to say where it is. Named here,
    /// rather than skipping that file, so the rest of the page is still held.</summary>
    private const string TheOldRoot = "/var/lib/shelfaware/models";

    [Fact]
    public void Every_pasted_install_installs_into_the_root_the_bootstrap_uses()
    {
        var root = PastedInstalls.BootstrapModelsRoot();
        var wrong = new List<string>();
        var bashBlocks = 0;

        foreach (var site in Sites.Where(s => s.EndsWith(".md", StringComparison.Ordinal)))
            foreach (var block in PastedInstalls.In(File.ReadAllText(RepoTree.FileAt(site))).Where(b => b.IsBash))
            {
                bashBlocks++;
                var named = Regex.Match(block.Code, @"\bM=(\S+)");
                if (!named.Success || named.Groups[1].Value != root)
                    wrong.Add($"{Path.GetFileName(site)} / {block.Archive} — installs into "
                              + $"{(named.Success ? named.Groups[1].Value : "no M= at all")}");
            }

        // Reach guard: five bash installs (Kokoro, Moonshine, Piper, and the bake-off's Kitten and
        // Matcha). A parse that stopped matching would otherwise pass having compared nothing.
        Assert.True(bashBlocks >= 5,
            $"Only {bashBlocks} bash install block(s) found across the docs — the scan is broken, not the docs.");

        Assert.True(wrong.Count == 0,
            $"A pasted install puts a model somewhere other than {root}, which is where the deploy's "
            + "bootstrap installs and where every box is pointed. It would unpack somewhere real and fail "
            + "nothing, leaving a box half-moved:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Every_documented_model_directory_sits_under_that_root()
    {
        var root = PastedInstalls.BootstrapModelsRoot();
        var wrong = new List<string>();
        var settings = 0;

        foreach (var site in Sites)
            foreach (Match m in Regex.Matches(File.ReadAllText(RepoTree.FileAt(site)),
                                              @"^#?\s*Speech__\w+__ModelDirectory=(\S+)", RegexOptions.Multiline))
            {
                settings++;
                // The root itself, then a separator: a path merely STARTING with it would let
                // /opt/shelfaware-models-old through, which is exactly the leftover a half-migration makes.
                if (!m.Groups[1].Value.StartsWith(root + "/", StringComparison.Ordinal))
                    wrong.Add($"{Path.GetFileName(site)} — {m.Groups[1].Value}");
            }

        // Reach guard: three docs and two env examples name one between them, twelve in all, and a voice
        // added only ever adds more.
        Assert.True(settings >= 12,
            $"Only {settings} Speech__*__ModelDirectory line(s) found — the scan is broken, not the docs.");

        Assert.True(wrong.Count == 0,
            $"A documented Speech__*__ModelDirectory points outside {root}, the root the deploy's bootstrap "
            + "installs into. A box configured from it boots, speaks, and is reading models from a directory "
            + "the app itself can replace:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// ⚠️ The old location survives in exactly one place: the steps for moving a box that is already
    /// running. Anywhere else it is a site the move missed.
    ///
    /// <para>This is the rule the other two cannot hold. They check that what a site says is right; this
    /// checks that nothing still says the old thing — a paragraph of prose explaining the wrong path, or
    /// a <c>rm -rf</c> aimed at it, is not a setting either of them parses.</para>
    /// </summary>
    [Fact]
    public void Only_the_migration_steps_still_name_the_old_location()
    {
        var migration = Path.Combine("docs", "deploy-droplet.md");
        var stragglers = new List<string>();
        var inTheMigration = 0;

        foreach (var site in Sites)
        {
            var lines = File.ReadAllLines(RepoTree.FileAt(site));
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(TheOldRoot, StringComparison.Ordinal)) continue;
                if (site == migration) inTheMigration++;
                else stragglers.Add($"{Path.GetFileName(site)}:{i + 1} — {lines[i].Trim()}");
            }
        }

        // Reach guard, and the one that matters most here: a rule whose whole output is "nothing found"
        // has to prove it looked. The migration steps name the old location, so if even those have gone
        // this rule is hunting a string nothing contains and would pass over any number of stragglers.
        Assert.True(inTheMigration > 0,
            $"docs/deploy-droplet.md no longer names {TheOldRoot} anywhere, so either the migration steps "
            + "have been removed — every box is moved, and this rule and TheOldRoot should go with them — "
            + "or this rule is now searching for something nothing contains and proves nothing.");

        Assert.True(stragglers.Count == 0,
            $"{TheOldRoot} is still named outside the migration steps in docs/deploy-droplet.md. The models "
            + "moved because that directory sits in the app's own home; a site left pointing at it is a box "
            + "left exposed, and it will boot and speak exactly as if it were not:"
            + Environment.NewLine + string.Join(Environment.NewLine, stragglers));
    }
}
