using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Speech;

/// <summary>One piece of a recipe as it gets read aloud. <paramref name="Name"/> is what a human would
/// call it ("intro", "step-3"); <paramref name="Text"/> is what actually goes to the synthesizer.</summary>
public sealed record NarrationSegment(string Name, string Text);

/// <summary>
/// How a recipe becomes the sequence of things said out loud. Segment 0 is the spoken intro; 1..N are the
/// steps, so a segment index maps straight onto a step number.
///
/// This lives here, rather than inside the reader, because the speech cache keys every clip on its text
/// AND its neighbours — so anything that wants to FIND a clip already in the cache has to segment the
/// recipe the exact same way the reader did when it put the clip there. Two copies of that rule would
/// drift, and the symptom would be silent: an export that quietly finds no audio.
/// </summary>
public static class RecipeNarration
{
    public static IReadOnlyList<NarrationSegment> Of(Recipe recipe) => Of(recipe.Name, recipe.Blurb, recipe.Steps);

    /// <summary>The same rule, for a caller holding a recipe's parts rather than a loaded entity — the
    /// export reads its rows flat (no navigations, so the JSON can't cycle) and shouldn't have to graft
    /// steps back onto a Recipe just to ask this question.</summary>
    public static IReadOnlyList<NarrationSegment> Of(string name, string? blurb, IEnumerable<RecipeStep> steps)
    {
        var intro = string.IsNullOrWhiteSpace(blurb) ? $"{name}." : $"{name}. {blurb}";
        var segments = new List<NarrationSegment> { new("intro", intro) };
        segments.AddRange(steps
            .OrderBy(s => s.Order)
            .Select((s, i) => new NarrationSegment($"step-{i + 1}", $"Step {i + 1}. {s.Text}")));
        return segments;
    }

    /// <summary>The neighbouring segments, so the provider can carry intonation across the cut — and so
    /// the cache key is the same one the reader computed.</summary>
    public static SpeechContext ContextAt(IReadOnlyList<NarrationSegment> segments, int i) =>
        new(Previous: i > 0 ? segments[i - 1].Text : null,
            Next: i + 1 < segments.Count ? segments[i + 1].Text : null);
}
