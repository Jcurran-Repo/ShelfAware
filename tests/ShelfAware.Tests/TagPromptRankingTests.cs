using ShelfAware.Core.Tagging;

namespace ShelfAware.Tests;

/// <summary>
/// The ranking an advisor prompt is trimmed by: the <see cref="TagVocabulary.PromptVocabularyLimit"/>
/// entries nearest the thing being asked about, by the cheap matcher's own distance. ⚠️ The bound exists
/// because the whole vocabulary used to ride into every charged call, unbounded in its size; it is a
/// RANKING and not a <c>Take</c> because a <c>Take</c> keeps whichever tags sort first and silently
/// degrades the synonym check for exactly the households with the most tags.
/// </summary>
public class TagPromptRankingTests
{
    [Fact]
    public void The_nearest_entries_come_first()
    {
        // From "kitten": kitten is 0 edits away, mitten 1, kitchen 2, sitting 3 — listed farthest first,
        // so the order out can only come from the distance.
        Assert.Equal(["Kitten", "Mitten", "Kitchen", "Sitting"],
            TagVocabulary.NearestForPrompt("kitten", ["Sitting", "Kitchen", "Mitten", "Kitten"]));
    }

    [Fact]
    public void Ties_keep_their_input_order()
    {
        // Pun, Pan and Pin are each one edit from "pen"; Pasta is four. The far one sorts last, and the
        // three equals come back exactly as they went in — a stable order, not a hash order.
        Assert.Equal(["Pun", "Pan", "Pin", "Pasta"],
            TagVocabulary.NearestForPrompt("Pen", ["Pasta", "Pun", "Pan", "Pin"]));
    }

    [Fact]
    public void The_limit_is_honoured_and_the_nearest_entry_is_kept_whatever_its_position()
    {
        // The nearest tag is LAST, where a plain Take would drop it; every filler is equally far from
        // "soda" (no digit matches a letter), so the ones that give way are the last-listed fillers.
        var fillers = Enumerable.Range(0, TagVocabulary.PromptVocabularyLimit + 10).Select(i => $"Filler {i:00}").ToList();
        var existing = fillers.Append("Sodium").ToList();

        var sent = TagVocabulary.NearestForPrompt("Soda", existing);

        Assert.Equal(TagVocabulary.PromptVocabularyLimit, sent.Count);
        Assert.Equal("Sodium", sent[0]);
        Assert.Equal(fillers.Take(TagVocabulary.PromptVocabularyLimit - 1), sent.Skip(1));
    }

    [Fact]
    public void A_vocabulary_under_the_limit_comes_back_whole()
    {
        var sent = TagVocabulary.NearestForPrompt("Soda", TagVocabulary.Seed);

        Assert.Equal(
            TagVocabulary.Seed.OrderBy(t => t, StringComparer.Ordinal),
            sent.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void An_entry_past_the_tag_cap_is_left_out_and_one_at_it_is_kept()
    {
        // The same predicate FindNearDuplicate skips by, measured on the trimmed entry — so a tag of
        // exactly the cap plus a trailing space is still a tag here, and comes back in its own spelling.
        var atTheLimit = new string('y', TagVocabulary.MaxLength);
        var monster = new string('x', TagVocabulary.MaxLength + 1);

        var sent = TagVocabulary.NearestForPrompt("Soda", [$"{atTheLimit} ", monster, "Soft Drink"]);

        Assert.Equal(2, sent.Count);
        Assert.Contains($"{atTheLimit} ", sent);
        Assert.DoesNotContain(monster, sent);
    }

    [Fact]
    public void An_entry_is_as_near_as_its_nearest_probe()
    {
        // ⚠️ NEAREST probe, not farthest. Each entry is within two edits of one probe and ten or more from
        // the other, and the far distances order them the other way round (9 against 10), so a ranking
        // that took the farthest probe would swap them. The recipe advisor relies on this: "Pasta" should
        // rank first for a pasta dish however far it is from the dish's name.
        string[] probes = ["aaaaaaaaaa", "bbbbb"];

        Assert.Equal(["aaaaaaaaaaa", "bbbbbbb"],
            TagVocabulary.NearestForPrompt(probes, ["bbbbbbb", "aaaaaaaaaaa"]));
        Assert.Equal("Pasta",
            TagVocabulary.NearestForPrompt(["Spaghetti", "pasta", "tomato"], ["Dinner", "Italian", "Pasta"])[0]);
    }

    [Fact]
    public void A_blank_probe_contributes_nothing()
    {
        // Both entries are six edits from "zzzzzz". A blank probe that counted would put "ab" first at
        // distance two (its own length); it must not.
        Assert.Equal(["abcdef", "ab"], TagVocabulary.NearestForPrompt(["   ", "zzzzzz"], ["abcdef", "ab"]));
    }

    [Fact]
    public void With_no_usable_probe_the_input_order_stands()
    {
        Assert.Equal(["Sodium", "Soda", "Pasta"], TagVocabulary.NearestForPrompt([""], ["Sodium", "Soda", "Pasta"]));
    }

    [Fact]
    public void A_probe_longer_than_a_tag_is_read_to_the_cap()
    {
        // A recipe's name is a probe and nothing caps it. Its first MaxLength characters are what count:
        // the entry equal to the head is at distance zero, and the entry equal to the tail — which the
        // probe also contains — is as far as a tag can be. Read whole, the two would tie.
        var head = new string('x', TagVocabulary.MaxLength);
        var tail = new string('z', TagVocabulary.MaxLength);

        Assert.Equal([head, tail], TagVocabulary.NearestForPrompt(head + tail, [tail, head]));
    }
}
