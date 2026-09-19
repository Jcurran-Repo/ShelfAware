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
/// <para>⚠️ And the first version of THIS file got the line itself wrong, which is the more interesting
/// half. It asked "is the reply empty once periods and spaces are stripped?" — a parser's convenience
/// promoted into a billing predicate, under which "." refunded and "!" was charged in full. The rule is
/// about whether the model said anything, so the test is whether anything it said carries meaning.</para>
/// </summary>
internal static class ProviderReply
{
    /// <summary>⚠️ Whether the model answered AT ALL — the money question. A reply with no letter and no
    /// digit in it is a provider that produced nothing: blank, or punctuation it emitted on the way to
    /// saying nothing. That act is refunded. Anything else is an answer and is paid for,
    /// <see cref="IsNothingFound"/> included — "none of these" is a considered answer to what the
    /// household asked, not a failure to give one.</summary>
    public static bool IsAnAnswer(string? reply) => reply is not null && reply.Any(char.IsLetterOrDigit);

    /// <summary>Whether the answer was the model's "nothing fits" sentinel. ⚠️ A CONTENT question, never
    /// a billing one. Trailing punctuation is ignored because the model routinely appends a period, and
    /// "NONE." has to read as the sentinel rather than as a literal tag called "NONE" joining the
    /// household's vocabulary.</summary>
    public static bool IsNothingFound(string? reply) =>
        (reply ?? string.Empty).Trim().TrimEnd('.', ' ').Equals("NONE", StringComparison.OrdinalIgnoreCase);
}
