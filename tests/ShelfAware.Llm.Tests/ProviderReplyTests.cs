namespace ShelfAware.Llm.Tests;

/// <summary>
/// ⚠️ Direct cover for the predicate that decides whether an AI act is paid for or given back.
///
/// <para>It had no test of its own for three rounds, and was wrong in all three: it asked "is the reply
/// empty once periods are stripped?" (so "." refunded and "!" was charged), then
/// <c>Any(char.IsLetterOrDigit)</c> (so an emoji-only reply was rendered, spoken aloud, and refunded in
/// full), then a deny-list carrying an arm that could never fire over a case that fell through and was
/// charged. Every one of those was found by a review rather than by a test, because every input reached
/// it through an advisor and no case in any suite distinguished the readings.</para>
///
/// <para>The mutation gate is scoped to <c>ShelfAware.Core</c> and does not reach this file, so the
/// category arms are pinned here one at a time. Deleting any arm of the switch has to turn something
/// red.</para>
/// </summary>
public class ProviderReplyTests
{
    [Theory]
    // Letters and digits, across the planes — the uncontroversial half.
    [InlineData("yes")]
    // ⚠️ Uppercase has its own line because it has its own arm, and "NONE" — the single most common
    // reply these four advisors get, and by §4.w an answer that is PAID FOR — is all uppercase. Without
    // this case, deleting `UppercaseLetter` from the switch left every suite green while every NONE
    // refunded, because the advisors answer a sentinel and a refund with the same null.
    [InlineData("NONE")]
    [InlineData("OK")]
    [InlineData("\u01C5")]              // titlecase letter (Lt)
    [InlineData("\u02B0")]              // modifier letter (Lm)
    [InlineData("\u02C7")]              // modifier symbol (Sk)
    [InlineData("\U0001F3FB")]          // skin-tone modifier (Sk) \u2014 a surrogate pair in that arm
    [InlineData("7")]
    [InlineData("東")]                  // BMP ideograph (Lo)
    [InlineData("\U00020000")]          // astral ideograph — two code units, neither one a letter
    [InlineData("٣")]                   // Arabic-Indic digit (Nd)
    [InlineData("Ⅳ")]                   // Roman numeral (Nl)
    [InlineData("½")]                   // vulgar fraction (No)
    // Symbols. A household reads these as meaning something, so they are answers.
    [InlineData("\U0001F44D")]          // \U0001F44D  (So, surrogate pair)
    [InlineData("✓")]                   // (So)
    [InlineData("→")]                   // (Sm)
    [InlineData("≥")]                   // (Sm)
    [InlineData("$")]                   // (Sc)
    [InlineData("\U0001F469\u200D\U0001F373")] // ZWJ sequence — the base emoji carries it
    [InlineData("\U0001F1EB\U0001F1F7")]       // regional indicators (a flag)
    [InlineData("  ok  ")]              // content survives surrounding space
    public void A_reply_carrying_anything_readable_is_an_answer(string reply) =>
        Assert.True(ProviderReply.IsAnAnswer(reply));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]              // Cc
    [InlineData("\u00A0")]              // no-break space (Zs)
    [InlineData("\uFEFF")]              // BOM / zero-width no-break space (Cf)
    [InlineData("\u200B")]              // zero-width space (Cf)
    // Punctuation, every class — what a model emits on the way to saying nothing.
    [InlineData(".")]
    [InlineData("!")]
    [InlineData("…")]                   // (Po)
    [InlineData("-")]                   // (Pd)
    [InlineData("—")]                   // em dash (Pd) — the doc claims this one by name
    [InlineData("_")]                   // (Pc)
    [InlineData("(")]                   // (Ps)
    [InlineData(")")]                   // (Pe)
    [InlineData("\u2018")]              // (Pi)
    [InlineData("\u2019")]              // (Pf)
    [InlineData(" . . . ")]
    // Marks with nothing to sit on: they render as nothing, so they are not an answer.
    [InlineData("\u0301")]              // combining acute (Mn)
    [InlineData("\uFE0F")]              // orphaned variation selector (Mn) — the reachable truncation case
    [InlineData("\u20E3")]              // orphaned enclosing keycap (Me)
    [InlineData("\u0903")]              // spacing combining mark (Mc)
    [InlineData("\uE000")]              // private use (Co)
    public void A_reply_with_nothing_readable_in_it_is_not_an_answer(string reply) =>
        Assert.False(ProviderReply.IsAnAnswer(reply));

    // Ill-formed UTF-16. EnumerateRunes substitutes U+FFFD, whose own category is OtherSymbol — so the
    // deny-list version classified mojibake as content and charged for it.
    //
    // ⚠️ These two are FACTS, not [InlineData] rows, and that is not a style choice. xUnit derives a
    // theory row's id from its arguments RENDERED AS TEXT, and a lone surrogate renders as U+FFFD — so
    // these two rows produced the same id, xUnit dropped one as a duplicate, and the summary still said
    // "Skipped: 0". One of the two cases this whole allow-list rests on was silently not running while
    // the suite reported green. A green a control has not earned is worse than a gap that is written
    // down, because the next person stops looking. Found by the pre-merge gate, 2026-09-19.
    [Fact]
    public void A_lone_surrogate_is_not_an_answer() =>
        // A \U0001F44D cut mid-pair by a truncated stream.
        Assert.False(ProviderReply.IsAnAnswer("\uD83D"));

    [Fact]
    public void A_reply_that_is_only_replacement_characters_is_not_an_answer() =>
        // A decode failure — distinct from the case above, which is what the shared id was hiding.
        Assert.False(ProviderReply.IsAnAnswer("\uFFFD\uFFFD\uFFFD"));

    [Fact]
    public void A_mark_is_not_an_answer_alone_but_costs_nothing_on_the_letter_it_belongs_to() =>
        Assert.True(ProviderReply.IsAnAnswer("e\u0301"));

    [Fact]
    public void The_two_separators_that_cannot_be_written_as_an_attribute_are_not_answers()
    {
        // U+2028 and U+2029 are line terminators to the C# LEXER even when escaped, so they cannot be
        // spelled in an [InlineData] string. Built from their code points instead rather than skipped:
        // Zl and Zp are two of the arms this file exists to pin.
        Assert.False(ProviderReply.IsAnAnswer(((char)0x2028).ToString()));
        Assert.False(ProviderReply.IsAnAnswer(((char)0x2029).ToString()));
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("none")]
    [InlineData("NONE.")]
    [InlineData(" None. ")]
    public void The_sentinel_is_read_through_the_punctuation_a_model_appends(string reply) =>
        Assert.True(ProviderReply.IsNothingFound(reply));

    [Theory]
    [InlineData("None of these")]
    [InlineData("NONE!")]               // only a trailing period is a model's habit; this is a sentence
    [InlineData("")]
    public void Anything_else_is_not_the_sentinel(string reply) =>
        Assert.False(ProviderReply.IsNothingFound(reply));
}
