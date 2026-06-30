namespace ShelfAware.Core.Tagging;

/// <summary>
/// Second-stage, LLM-backed tag dedup: catches semantic synonyms the plain-code
/// <see cref="TagVocabulary.FindNearDuplicate"/> can't (e.g. "Soda" ≈ "Soft Drink", "Cleaner" ≈
/// "Detergent"). Only called when the cheap check finds nothing — language understanding where it's
/// genuinely required, plain code where it suffices.
/// </summary>
public interface ITagAdvisor
{
    /// <summary>Returns an existing tag that means the same thing as <paramref name="candidate"/>, or
    /// null if it's genuinely distinct. Never throws — returns null on any error so tag creation isn't blocked.</summary>
    Task<string?> FindSynonymAsync(string candidate, IReadOnlyList<string> existing, CancellationToken cancellationToken = default);
}
