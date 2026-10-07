namespace ShelfAware.Core;

/// <summary>
/// ⚠️ THE one place "how much free text may a household put into a charged prompt?" is answered — the
/// sibling of <see cref="Tagging.TagVocabulary.MaxLength"/> for the three boxes that take prose: the
/// dashboard's quick update, the recipe request, and a pasted recipe. Each flows straight into a provider
/// call whose price is flat per act while its cost is per token, so an unbounded box is an unbounded
/// per-call cost the household controls (docs/backlog.md, "The recipe request box has no cap"). A
/// <c>maxlength</c> attribute on the input is the courtesy; the guard that counts is the server-side
/// <see cref="IsOverLength"/> the page's handler asks before its pre-check, because a client attribute is
/// one devtools edit from gone.
///
/// <para>The caps are generous for what each box is FOR — a spoken sentence or three, a mood for dinner,
/// a recipe page — and tight against the shape they refuse: a pasted novel. Changing one changes the
/// message with it, so no screen can quote a number the guard no longer holds.</para>
/// </summary>
public static class PromptInput
{
    /// <summary>A quick update: "we're out of dog food, almost out of coffee, bought eggs" is ~60 characters;
    /// the assistant handles several statements per line, and 500 holds a dozen.</summary>
    public const int ChatMaxLength = 500;

    /// <summary>A recipe request: a mood, a constraint, a craving. 300 is a long one.</summary>
    public const int RecipeRequestMaxLength = 300;

    /// <summary>A pasted recipe: a long recipe page with headnotes runs 5–8k characters; 20k leaves room
    /// for a very long one and refuses a cookbook.</summary>
    public const int PastedRecipeMaxLength = 20_000;

    /// <summary>True when <paramref name="text"/>, trimmed, is longer than <paramref name="max"/>. Trimmed,
    /// so trailing whitespace is never what tips a legitimate paste over — the same rule
    /// <see cref="Tagging.TagVocabulary.IsOverLength"/> applies to a tag.</summary>
    public static bool IsOverLength(string? text, int max) => (text?.Trim().Length ?? 0) > max;

    /// <summary>What a screen says when <see cref="IsOverLength"/> refuses — quoting the cap it was judged
    /// against, formatted with a thousands separator so "20,000" reads as a number and not a typo.</summary>
    public static string TooLongMessage(int max) =>
        $"That's too long — keep it to {max:N0} characters or fewer.";
}
