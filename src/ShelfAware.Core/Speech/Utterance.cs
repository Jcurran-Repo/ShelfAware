namespace ShelfAware.Core.Speech;

/// <summary>
/// Reduces a raw transcript to the phrase we match commands against: lower-cased, punctuation dropped,
/// and leading/trailing filler stripped, so "Okay, next step please" and "next step" are one thing.
///
/// Shared by every plain-code command matcher so they agree on what an utterance IS — two matchers with
/// slightly different ideas of "filler" would be a bug nobody could see.
/// </summary>
internal static class Utterance
{
    /// <summary>
    /// Filler tolerated at either end of a command. Deliberately short: every word added here is a word
    /// that silently disappears from the middle-ground phrases too, so a bare "sec" or "then" has to be
    /// spelled out in the phrase lists rather than assumed. Nothing here may carry meaning on its own.
    /// </summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "ok", "okay", "alright", "please", "now", "thanks", "thank", "you", "and",
        "um", "uh", "er", "hey", "hi", "so",
    };

    /// <summary>
    /// The command phrase inside <paramref name="transcript"/>, or "" if there's nothing left once
    /// filler is stripped. An all-filler utterance ("okay") deliberately yields "" rather than being
    /// read as agreement — someone muttering to themselves shouldn't advance the recipe.
    /// </summary>
    public static string Core(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return "";

        var tokens = Tokenize(transcript);
        int start = 0, end = tokens.Count;
        while (start < end && Filler.Contains(tokens[start])) start++;
        while (end > start && Filler.Contains(tokens[end - 1])) end--;
        return string.Join(' ', tokens.Skip(start).Take(end - start));
    }

    /// <summary>Word count of the raw transcript, before filler is stripped.</summary>
    public static int WordCount(string? transcript) =>
        string.IsNullOrWhiteSpace(transcript) ? 0 : Tokenize(transcript).Count;

    private static List<string> Tokenize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '\'' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
}
