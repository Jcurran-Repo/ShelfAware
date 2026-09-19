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
    /// household's vocabulary.
    ///
    /// <para>⚠️ This is NOT "which existing name does the reply mean?" — that question belongs to
    /// <see cref="ShelfAware.Core.Tagging.TagVocabulary"/>, which says in its own remarks that it is the
    /// one place the dedup policy lives. A <c>Names</c> helper lived here for one commit and was a THIRD
    /// reading of it, narrower than the one that already claimed to be the only one: it knew about a
    /// trailing period and not about case-folding, collapsed whitespace, a plural "s" or a one-character
    /// typo, so a reply of "Soft Drinks" coined a duplicate the plain-code stage had already caught eight
    /// lines earlier in the same request. Asking whether a reply is the sentinel is a different question
    /// and stays here.</para></summary>
    public static bool IsNothingFound(string reply) =>
        reply.Trim().TrimEnd('.', ' ').Equals("NONE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether one rune is something the household would read as content rather than as spacing,
    /// punctuation or a mark with nothing to sit on.
    ///
    /// <para>⚠️ An ALLOW-list, and the deny-list it replaced was wrong twice over in the one commit it
    /// lived. It carried an arm for <see cref="UnicodeCategory.Surrogate"/> that can never fire — a
    /// <see cref="Rune"/> cannot hold a surrogate code point, and <c>EnumerateRunes</c> substitutes
    /// U+FFFD for every ill-formed subsequence — while U+FFFD's own category is <c>OtherSymbol</c>, so
    /// the case the dead arm was written to refuse was the case that fell through and got charged. A
    /// money predicate cannot be a list of what to exclude: whatever the list forgets is billed.</para>
    ///
    /// <para>⚠️ Marks are not content on their own. A reply of a bare combining accent renders as nothing
    /// and would be charged for an empty bubble; attached to a letter it costs nothing to exclude, because
    /// the letter it sits on already answers the question.</para></summary>
    private static bool IsContent(Rune rune) =>
        rune != Rune.ReplacementChar
        && Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter => true,
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber => true,
            // A tick, an arrow and an emoji are all answers a household reads. Punctuation is what a model
            // emits on the way to saying nothing, and every other category is spacing or invisible.
            UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol => true,
            _ => false,
        };
}
