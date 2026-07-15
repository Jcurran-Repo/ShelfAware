using Microsoft.Extensions.Options;
using ShelfAware.Web.Ingest;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The receipt folder setting is a path a signed-in user types, and the inbox opens every image and PDF
/// it finds there. On a self-host that's the feature — the owner is the only tenant and the folder is
/// theirs. On a shared deployment it's an arbitrary-path read, so a root can be configured to confine it.
/// </summary>
public class ReceiptFolderPolicyTests
{
    private static ReceiptFolderPolicy Policy(string? allowedRoot) =>
        new(Options.Create(new ReceiptFolderOptions { AllowedRoot = allowedRoot }));

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sa-inbox");

    [Fact]
    public void Unconfigured_allows_any_local_path()
    {
        // The self-host default: the owner's receipts live wherever the owner keeps them.
        //
        // Built from GetTempPath rather than written out as C:\Users\... — CI runs on Linux, where a
        // string like that isn't an absolute path at all but a relative filename that happens to contain
        // backslashes and a colon, so GetFullPath resolves it under the working directory and Permit
        // rightly hands back somewhere else entirely.
        var policy = Policy(null);
        var theirs = Path.Combine(Path.GetTempPath(), "Documents", "Walmart Receipts");

        Assert.False(policy.IsConfined);
        Assert.Null(policy.Reject(theirs));
        Assert.Equal(theirs, policy.Permit(theirs));
    }

    [Fact]
    public void A_folder_inside_the_root_is_allowed()
    {
        var policy = Policy(Root);
        var inside = Path.Combine(Root, "household-a");

        Assert.True(policy.IsConfined);
        Assert.Null(policy.Reject(inside));
        Assert.Equal(inside, policy.Permit(inside));
    }

    [Fact]
    public void The_root_itself_is_allowed()
    {
        Assert.Null(Policy(Root).Reject(Root));
    }

    /// <summary>Absolute on either platform, and genuinely elsewhere: a sibling of the allowed root.
    /// A hardcoded "C:\Windows\Temp" is refused on Linux too, but only because it resolves to a relative
    /// path under the working directory — passing for a reason the test isn't about.</summary>
    private static readonly string Elsewhere = Path.Combine(Path.GetTempPath(), "sa-somewhere-else");

    [Fact]
    public void A_folder_outside_the_root_is_refused()
    {
        var policy = Policy(Root);

        Assert.NotNull(policy.Reject(Elsewhere));
        Assert.Null(policy.Permit(Elsewhere));
    }

    [Fact]
    public void Walking_out_with_dot_dot_is_refused()
    {
        // The whole reason the check resolves the path first: without it this reads as being under the root.
        var escape = Path.Combine(Root, "..", "..", "Windows");

        Assert.NotNull(Policy(Root).Reject(escape));
    }

    [Fact]
    public void Walking_out_and_back_in_is_allowed_because_it_lands_inside()
    {
        var roundabout = Path.Combine(Root, "a", "..", "b");

        Assert.Null(Policy(Root).Reject(roundabout));
    }

    [Fact]
    public void A_sibling_sharing_the_roots_name_prefix_is_refused()
    {
        // "<root>-old" starts with "<root>" as a string but is not inside it. This is why the check
        // requires a separator after the root.
        Assert.NotNull(Policy(Root).Reject(Root + "-old"));
    }

    [Fact]
    public void A_unc_path_is_refused_when_confined()
    {
        Assert.NotNull(Policy(Root).Reject(@"\\fileserver\share\receipts"));
    }

    [Fact]
    public void An_empty_folder_is_not_an_error_it_just_means_the_feature_is_off()
    {
        var policy = Policy(Root);

        Assert.Null(policy.Reject(""));
        Assert.Null(policy.Reject(null));
        Assert.Null(policy.Permit(""));
    }

    [Fact]
    public void A_stored_folder_that_is_no_longer_allowed_reads_as_no_folder()
    {
        // The inbox re-checks rather than trusting the table: a value can outlive the rules in force when
        // it was written (an older build, a hand-edited DB).
        Assert.Null(Policy(Root).Permit(Elsewhere));
    }

    [Fact]
    public void Permit_returns_the_resolved_path_not_the_string_it_was_given()
    {
        // The path that gets opened must be the exact one that was checked. Returning the raw input worked
        // only because .NET happened to resolve it the same way at open time.
        var roundabout = Path.Combine(Root, "a", "..", "b");

        Assert.Equal(Path.Combine(Root, "b"), Policy(Root).Permit(roundabout));
    }

    [Fact]
    public void An_unconfined_permit_also_returns_a_resolved_path()
    {
        var roundabout = Path.Combine(Path.GetTempPath(), "x", "..", "y");

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), "y")),
            Policy(null).Permit(roundabout));
    }

    [Fact]
    public void A_drive_root_as_the_allowed_root_still_contains_things()
    {
        // "Anything on this drive" is the loosest confinement that still counts as confinement, and it's
        // what someone types when they want it. A root already ends in a separator, so appending another
        // built a prefix no real path has and refused the entire volume.
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var policy = Policy(driveRoot);

        Assert.True(policy.IsConfined);
        Assert.Null(policy.Reject(Path.Combine(driveRoot, "anywhere", "at", "all")));
        Assert.Null(policy.Reject(driveRoot));
    }

    [Fact]
    public void An_unresolvable_path_is_refused_even_when_nothing_is_confined()
    {
        // A path that can't be resolved can't be read either — saying so beats saving it and silently
        // importing nothing. (The old code only looked at the path at all when confined.)
        Assert.NotNull(Policy(null).Reject("C:\\bad\0path"));
        Assert.Null(Policy(null).Permit("C:\\bad\0path"));
    }

    [Fact]
    public void A_root_that_cannot_be_resolved_is_reported_rather_than_silently_unconfining()
    {
        // The failure this guards is silent: an unusable root leaves nothing to enforce, and "nothing to
        // enforce" looks identical to "confinement was never wanted". Program.cs fails startup on it.
        Assert.True(ReceiptFolderPolicy.RootIsUsable(Root));
        Assert.True(ReceiptFolderPolicy.RootIsUsable(null));      // not configured: legitimately unconfined
        Assert.True(ReceiptFolderPolicy.RootIsUsable("   "));
        Assert.False(ReceiptFolderPolicy.RootIsUsable("C:\\bad\0path"));
    }
}
