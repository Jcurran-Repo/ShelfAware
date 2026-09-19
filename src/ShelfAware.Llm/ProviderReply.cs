using System.Globalization;
using System.Text;

namespace ShelfAware.Llm;

/// <summary>
/// How a short prose reply from the model is read — ONE definition for the advisors that ask the model a
/// question in words and get a line back, and for the chat turn's own final reply.
///
/// <para>⚠️ <see cref="IsAnAnswer"/> is a MONEY decision. An act that answered keeps its charge and an act
/// that got nothing back is refunded (<c>docs/subscription-plan.md</c> §4.w), so "did the model say
/// anything?" is a question with one right answer and five places asking it. They were allowed to ask it
/// their own way for exactly one commit, and in that commit three tested the raw reply while a fourth
/// tested it with trailing punctuation stripped — so a reply of "." refunded in one and was paid for in
/// the other three, while the doc beside them said all four drew the line in the same place. That is the
/// repo's oldest failure shape, found by a review rather than by a household.</para>
///
/// <para>⚠️ And the first two versions of THIS file got the line itself wrong, which is the more
/// interesting half. The first asked "is the reply empty once periods and spaces are stripped?" — a
/// parser's convenience promoted into a billing predicate, under which "." refunded and "!" was charged
/// in full. The second asked <c>Any(char.IsLetterOrDigit)</c>, which reads UTF-16 code units and is false
/// for BOTH halves of a surrogate pair: a reply of "👍" or "✅" was rendered in the chat box, read by the
/// household, and refunded in full. So was "✓", "→" and every reply written in an astral script. Each
/// version was a narrower question than the one being asked, and each one shipped with a comment
/// explaining why its narrower question was the right one.</para>
/// </summary>
internal static class ProviderReply
{
    /// <summary>⚠️ Whether the model answered AT ALL — the money question. Anything the household could
    /// read as content is an answer and is paid for, <see cref="IsNothingFound"/> included: "none of
    /// these" is a considered answer to what the household asked, not a failure to give one. Only a reply
    /// made entirely of whitespace, punctuation and invisibles is a provider that produced nothing, and
    /// that act is refunded.
    ///
    /// <para>⚠️ By RUNE, not by <c>char</c>. An emoji is two code units and neither half is a letter, so
    /// a per-char test refunds "👍" — a reply the chat box rendered and a voice surface spoke.</para>
    ///
    /// <para>⚠️ Symbols COUNT as content and punctuation does not, which is the line itself rather than an
    /// implementation detail. "✓" and "→" are replies a household reads as meaning something; "." and "…"
    /// and "—" are what a model emits on its way to saying nothing.</para></summary>
    public static bool IsAnAnswer(string reply) => reply.EnumerateRunes().Any(IsContent);

    /// <summary>Whether the answer was the model's "nothing fits" sentinel. ⚠️ A CONTENT question, never
    /// a billing one. Trailing punctuation is ignored because the model routinely appends a period, and
    /// "NONE." has to read as the sentinel rather than as a literal tag called "NONE" joining the
    /// household's vocabulary.</summary>
    public static bool IsNothingFound(string reply) => Sentence(reply).Equals("NONE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="reply"/> names <paramref name="candidate"/>, ignoring case and a
    /// trailing period on either side.
    ///
    /// <para>⚠️ Both sides, and the two attempts at this got it wrong in opposite directions. The prompt
    /// asks for an existing name back "EXACTLY as written", so the household's own spelling is the thing
    /// being matched — and a tag can legitimately end in a period ("Etc."), which a reply of "Etc" has to
    /// find. The model also routinely appends one, so "Soft Drink." has to find "Soft Drink". Matching on
    /// the raw reply alone misses the second; matching on the stripped reply alone misses the first. The
    /// first fix here chose one, the second chose the other, and both shipped a comment explaining why
    /// their half was the important one.</para></summary>
    public static bool Names(string reply, string candidate) =>
        string.Equals(candidate.Trim(), reply.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(Sentence(candidate), Sentence(reply), StringComparison.OrdinalIgnoreCase);

    /// <summary>A reply with its trailing sentence punctuation and space taken off.</summary>
    private static string Sentence(string text) => text.Trim().TrimEnd('.', ' ');

    /// <summary>Whether one rune is something the household would read as content rather than as spacing
    /// or punctuation. Letters, digits, marks and symbols yes; separators, controls, formats and every
    /// punctuation class no.</summary>
    private static bool IsContent(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation => false,
        _ => true,
    };
}
