namespace ShelfAware.Llm;

/// <summary>
/// How a short prose reply from the model is read — ONE definition for the four advisors that ask the
/// model a question in words and get one line back (product substitutes, ingredient alternatives, and
/// the two tag advisors).
///
/// <para>⚠️ It exists because <see cref="IsAnAnswer"/> is a MONEY decision. An act that answered keeps
/// its charge and an act that got nothing back is refunded (<c>docs/subscription-plan.md</c> §4.w), so
/// "did the model say anything?" is a question with one right answer and four places asking it. They
/// were allowed to ask it their own way for exactly one commit, and in that commit three of them tested
/// the raw reply while the fourth tested the reply with trailing punctuation stripped — so a reply of
/// "." refunded in one advisor and was paid for in the other three, while the doc beside them said all
/// four drew the line in the same place. That is the repo's oldest failure shape, found here by a review
/// rather than by a household, and it is the argument for this file existing at all.</para>
/// </summary>
internal static class ProviderReply
{
    /// <summary>The model's answer with trailing sentence punctuation and space removed. The model
    /// routinely appends a period, and "NONE." has to read as the sentinel rather than as a literal tag
    /// called "NONE" polluting the household's vocabulary.</summary>
    public static string Normalize(string? text) => (text ?? string.Empty).Trim().TrimEnd('.', ' ');

    /// <summary>⚠️ Whether the model answered AT ALL — the money question. A normalized reply with
    /// nothing left in it is a provider that produced no answer, and that act is refunded; anything else
    /// is an answer and is paid for, <see cref="IsNothingFound"/> included.</summary>
    public static bool IsAnAnswer(string normalized) => normalized.Length > 0;

    /// <summary>Whether the answer was the model's "nothing fits" sentinel. ⚠️ This is a CONTENT
    /// question, never a billing one: a considered "none of these" is an answer the household asked
    /// for and paid for.</summary>
    public static bool IsNothingFound(string normalized) =>
        normalized.Equals("NONE", StringComparison.OrdinalIgnoreCase);
}
