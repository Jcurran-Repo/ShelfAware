using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

/// <summary>
/// Pins the hands-free grammar. Two failure modes matter here and they pull in opposite directions:
/// missing a command sends a "next" to the model (slow, and it costs), while over-matching hijacks a
/// real question ("what's next after the salt") and answers the wrong one. The whole-utterance rule is
/// what holds the line, so most of these tests are about the line.
/// </summary>
public class CookAlongCommandsTests
{
    private static CookAlongIntent Intent(string? t) => CookAlongCommands.Match(t).Intent;

    [Theory]
    [InlineData("next")]
    [InlineData("Next step.")]
    [InlineData("okay next")]
    [InlineData("okay, next step please")]
    [InlineData("what's next")]
    [InlineData("What do I do next?")]
    [InlineData("keep going")]
    [InlineData("got it")]
    [InlineData("done")]
    [InlineData("and then")]
    public void Next_phrases_advance(string t) => Assert.Equal(CookAlongIntent.Next, Intent(t));

    // Jumping to a step is navigation, not a question — it must move the reader (instantly, from cache)
    // rather than going to the brain to be answered ABOUT.
    [Theory]
    [InlineData("step 3", 3)]
    [InlineData("Step 3.", 3)]
    [InlineData("go to step 3", 3)]
    [InlineData("Go to step three.", 3)]
    [InlineData("jump to step 5", 5)]
    [InlineData("read step 2", 2)]
    [InlineData("read me step seven", 7)]
    [InlineData("back to step 2", 2)]
    [InlineData("take me to step 4", 4)]
    [InlineData("okay, go to step 6 please", 6)]
    [InlineData("step twelve", 12)]
    [InlineData("first step", 1)]
    [InlineData("step one", 1)]
    [InlineData("start over", 0)]
    [InlineData("from the top", 0)]
    public void A_step_can_be_asked_for_by_name(string t, int expected)
    {
        var command = CookAlongCommands.Match(t);
        Assert.Equal(CookAlongIntent.GoToStep, command.Intent);
        Assert.Equal(expected, command.Step);
    }

    // Core doesn't know how long the recipe is, so an impossible step still parses — the caller hands it
    // to the brain, which does know and can say "there are only seven".
    [Fact]
    public void An_impossible_step_still_parses_and_is_left_for_the_caller() =>
        Assert.Equal(new CookAlongCommand(CookAlongIntent.GoToStep, 99), CookAlongCommands.Match("go to step 99"));

    // A cough is not a word. Scribe tags non-speech audio INTO the transcript ("Next (coughing)"), which
    // turned a one-word command into a two-word phrase that matched nothing and got sent to the model as
    // a question — the recipe just sat there. We ask Scribe not to tag, and refuse to be fooled anyway.
    [Theory]
    [InlineData("Next (coughing)", CookAlongIntent.Next)]
    [InlineData("(coughing) next", CookAlongIntent.Next)]
    [InlineData("next step (laughter)", CookAlongIntent.Next)]
    [InlineData("(clears throat) go back", CookAlongIntent.Back)]
    [InlineData("repeat [door closes]", CookAlongIntent.Repeat)]
    [InlineData("(sneezing) stop reading", CookAlongIntent.Stop)]
    public void Transcriber_annotations_are_not_words(string t, CookAlongIntent expected) =>
        Assert.Equal(expected, Intent(t));

    [Fact]
    public void An_annotated_step_jump_still_jumps()
    {
        var command = CookAlongCommands.Match("go to step 3 (coughing)");
        Assert.Equal(CookAlongIntent.GoToStep, command.Intent);
        Assert.Equal(3, command.Step);
    }

    // Nothing but annotations means nobody said anything — it must never advance the recipe, and must
    // never be worth a model call either.
    [Theory]
    [InlineData("(coughing)")]
    [InlineData("(laughter) (footsteps)")]
    [InlineData("[background noise]")]
    public void An_utterance_that_is_only_room_noise_does_nothing(string t)
    {
        Assert.Equal(CookAlongIntent.None, Intent(t));
        Assert.False(CookAlongCommands.IsWorthAsking(t));
    }

    // The window closes on silence, not a timer, so saying a command again before the pause elapses puts
    // both in one utterance. Impatience is not a different instruction.
    [Theory]
    [InlineData("next next", CookAlongIntent.Next)]
    [InlineData("Next. Next.", CookAlongIntent.Next)]
    [InlineData("next next next", CookAlongIntent.Next)]
    [InlineData("next step next step", CookAlongIntent.Next)]
    [InlineData("back back", CookAlongIntent.Back)]
    [InlineData("repeat repeat", CookAlongIntent.Repeat)]
    [InlineData("stop reading stop reading", CookAlongIntent.Stop)]
    [InlineData("okay next, next please", CookAlongIntent.Next)]
    public void Saying_a_command_twice_is_still_that_command(string t, CookAlongIntent expected) =>
        Assert.Equal(expected, Intent(t));

    [Fact]
    public void A_repeated_step_jump_still_jumps()
    {
        var command = CookAlongCommands.Match("step 3 step 3");
        Assert.Equal(CookAlongIntent.GoToStep, command.Intent);
        Assert.Equal(3, command.Step);
    }

    // Collapsing repetition must not be able to MAKE a command out of a sentence: if the repeated unit
    // isn't one, the result isn't either.
    [Theory]
    [InlineData("how much salt how much salt")]
    [InlineData("is it done is it done")]
    public void A_repeated_question_is_still_a_question(string t) => Assert.Equal(CookAlongIntent.None, Intent(t));

    // Two different commands in one breath is not repetition and must not be guessed at.
    [Fact]
    public void Two_different_commands_are_not_collapsed() => Assert.Equal(CookAlongIntent.None, Intent("next back"));

    // A step NUMBER inside a real question is still a question. The whole-utterance rule again.
    [Theory]
    [InlineData("what goes in at step 3")]
    [InlineData("how long is step 2")]
    [InlineData("do I need the oven for step 4")]
    [InlineData("is step 3 the one with the garlic")]
    public void A_question_that_mentions_a_step_is_still_a_question(string t) =>
        Assert.Equal(CookAlongIntent.None, Intent(t));

    [Theory]
    [InlineData("back")]
    [InlineData("go back")]
    [InlineData("previous step")]
    [InlineData("Back up.")]
    [InlineData("one step back")]
    public void Back_phrases_go_back(string t) => Assert.Equal(CookAlongIntent.Back, Intent(t));

    [Theory]
    [InlineData("repeat")]
    [InlineData("say that again")]
    [InlineData("one more time")]
    [InlineData("What was that?")]
    [InlineData("come again")]
    public void Repeat_phrases_repeat(string t) => Assert.Equal(CookAlongIntent.Repeat, Intent(t));

    [Theory]
    [InlineData("hold on")]
    [InlineData("wait")]
    [InlineData("hang on")]
    [InlineData("just a sec")]
    [InlineData("give me a minute")]
    [InlineData("pause")]
    public void Hold_phrases_hold(string t) => Assert.Equal(CookAlongIntent.Hold, Intent(t));

    [Theory]
    [InlineData("i'm back")]
    [InlineData("ready")]
    [InlineData("resume")]
    [InlineData("let's go")]
    public void Resume_phrases_resume(string t) => Assert.Equal(CookAlongIntent.Resume, Intent(t));

    [Theory]
    [InlineData("stop reading")]
    [InlineData("stop cooking")]
    [InlineData("never mind")]
    [InlineData("I'm done cooking.")]
    [InlineData("stop listening")]   // the general session-stop grammar counts here too
    [InlineData("goodbye")]
    [InlineData("that's all")]
    public void Stop_phrases_end_the_cook_along(string t) => Assert.Equal(CookAlongIntent.Stop, Intent(t));

    // The point of the whole-utterance rule: a real question that happens to contain a command word is
    // still a question. Hijacking these would answer the wrong thing and look broken.
    [Theory]
    [InlineData("what's next after the salt goes in")]
    [InlineData("can I use butter instead of oil")]
    [InlineData("should I wait for it to brown")]
    [InlineData("how long do I keep going")]
    [InlineData("do I add the garlic back in")]
    [InlineData("is it done")]
    [InlineData("how much salt")]
    [InlineData("we're out of paprika")]
    public void Real_questions_are_left_for_the_brain(string t) =>
        Assert.Equal(CookAlongIntent.None, Intent(t));

    // An all-filler mutter must not advance the recipe — someone talking to themselves isn't a command.
    [Theory]
    [InlineData("okay")]
    [InlineData("um")]
    [InlineData("alright")]
    [InlineData("uh, okay")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Filler_and_silence_do_nothing(string? t) =>
        Assert.Equal(CookAlongIntent.None, Intent(t));

    // Stop wins over everything: an utterance that ends the session must never be read as navigation.
    [Fact]
    public void Stop_is_matched_before_the_navigation_grammar() =>
        Assert.Equal(CookAlongIntent.Stop, Intent("stop the cook along"));

    [Theory]
    [InlineData("can I use butter instead", true)]
    [InlineData("how much salt", true)]
    [InlineData("mm", false)]                      // a stray syllable off the extractor fan
    [InlineData("uh", false)]
    [InlineData("mm mm", false)]                   // ...twice. Still nobody talking.
    [InlineData("um uh", false)]                   // two tokens of pure filler
    [InlineData("(coughing) (footsteps)", false)]  // two tokens of pure room
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_real_utterance_is_worth_waking_the_brain(string? t, bool expected) =>
        Assert.Equal(expected, CookAlongCommands.IsWorthAsking(t));

    // The existing session-stop grammar must keep behaving after sharing Utterance with the new matcher.
    [Theory]
    [InlineData("stop listening", true)]
    [InlineData("Okay, stop listening please", true)]
    [InlineData("we're out of milk, then stop listening", false)]
    [InlineData("stop", false)]
    public void The_session_stop_grammar_is_unchanged(string t, bool expected) =>
        Assert.Equal(expected, VoiceCommands.IsStop(t));

    // ---- Every phrase in every table, pinned. These grammars ARE the hands-free control surface; a
    //      dropped phrase silently sends a "next" to the model, so the honest close is one InlineData
    //      per entry. (Dead-once-normalized phrasings — "and then", "back now", "carry on now" — were
    //      removed, since Utterance.Core strips their trailing/leading filler and no input can reach them.)

    [Theory]
    [InlineData("next")] [InlineData("next step")] [InlineData("next one")] [InlineData("up next")]
    [InlineData("on to the next")] [InlineData("onto the next")] [InlineData("next up")]
    [InlineData("go on")] [InlineData("go ahead")] [InlineData("continue")] [InlineData("keep going")]
    [InlineData("carry on")] [InlineData("move on")] [InlineData("move along")] [InlineData("onward")]
    [InlineData("then")] [InlineData("then what")] [InlineData("what's next")] [InlineData("what is next")]
    [InlineData("whats next")] [InlineData("what do i do next")] [InlineData("what next")] [InlineData("done")]
    [InlineData("got it")] [InlineData("did it")] [InlineData("that's done")] [InlineData("thats done")]
    [InlineData("finished")] [InlineData("i'm done with that")] [InlineData("im done with that")]
    public void Every_next_phrase_advances(string t) => Assert.Equal(CookAlongIntent.Next, Intent(t));

    [Theory]
    [InlineData("back")] [InlineData("go back")] [InlineData("back up")] [InlineData("step back")]
    [InlineData("previous")] [InlineData("previous step")] [InlineData("last step")]
    [InlineData("go back a step")] [InlineData("one step back")] [InlineData("before that")]
    public void Every_back_phrase_goes_back(string t) => Assert.Equal(CookAlongIntent.Back, Intent(t));

    [Theory]
    [InlineData("repeat")] [InlineData("repeat that")] [InlineData("again")] [InlineData("say again")]
    [InlineData("say that again")] [InlineData("read that again")] [InlineData("one more time")]
    [InlineData("come again")] [InlineData("what was that")] [InlineData("what did you say")]
    [InlineData("sorry what")]
    public void Every_repeat_phrase_repeats(string t) => Assert.Equal(CookAlongIntent.Repeat, Intent(t));

    [Theory]
    [InlineData("hold on")] [InlineData("hold up")] [InlineData("wait")] [InlineData("wait a minute")]
    [InlineData("wait a sec")] [InlineData("wait a second")] [InlineData("hang on")] [InlineData("one second")]
    [InlineData("one sec")] [InlineData("just a second")] [InlineData("just a sec")]
    [InlineData("give me a second")] [InlineData("give me a minute")] [InlineData("pause")]
    [InlineData("stop for a second")] [InlineData("hold")]
    public void Every_hold_phrase_holds(string t) => Assert.Equal(CookAlongIntent.Hold, Intent(t));

    [Theory]
    [InlineData("i'm back")] [InlineData("im back")] [InlineData("ready")] [InlineData("i'm ready")]
    [InlineData("im ready")] [InlineData("resume")] [InlineData("let's go")] [InlineData("lets go")]
    [InlineData("go")]
    public void Every_resume_phrase_resumes(string t) => Assert.Equal(CookAlongIntent.Resume, Intent(t));

    [Theory]
    [InlineData("stop reading")] [InlineData("stop the recipe")] [InlineData("stop cooking")]
    [InlineData("stop the cook along")] [InlineData("stop cook along")] [InlineData("close the recipe")]
    [InlineData("i'm done cooking")] [InlineData("im done cooking")] [InlineData("never mind")]
    [InlineData("nevermind")] [InlineData("cancel")]
    public void Every_stop_phrase_ends_it(string t) => Assert.Equal(CookAlongIntent.Stop, Intent(t));

    [Theory]
    [InlineData("start over")] [InlineData("start again")] [InlineData("from the top")]
    [InlineData("start from the beginning")] [InlineData("begin again")] [InlineData("back to the start")]
    [InlineData("back to the beginning")]
    public void Every_start_over_phrase_jumps_to_the_intro(string t) =>
        Assert.Equal(new CookAlongCommand(CookAlongIntent.GoToStep, 0), CookAlongCommands.Match(t));

    [Theory]
    [InlineData("first step")]
    [InlineData("the first step")]
    public void Every_first_step_phrase_jumps_to_one(string t) =>
        Assert.Equal(new CookAlongCommand(CookAlongIntent.GoToStep, 1), CookAlongCommands.Match(t));

    // Speech-to-text may hand back "step 3" or "step three": every number word 0–20 resolves to its index.
    [Theory]
    [InlineData("zero", 0)] [InlineData("one", 1)] [InlineData("two", 2)] [InlineData("three", 3)]
    [InlineData("four", 4)] [InlineData("five", 5)] [InlineData("six", 6)] [InlineData("seven", 7)]
    [InlineData("eight", 8)] [InlineData("nine", 9)] [InlineData("ten", 10)] [InlineData("eleven", 11)]
    [InlineData("twelve", 12)] [InlineData("thirteen", 13)] [InlineData("fourteen", 14)]
    [InlineData("fifteen", 15)] [InlineData("sixteen", 16)] [InlineData("seventeen", 17)]
    [InlineData("eighteen", 18)] [InlineData("nineteen", 19)] [InlineData("twenty", 20)]
    public void A_step_number_word_resolves_to_its_index(string word, int expected) =>
        Assert.Equal(new CookAlongCommand(CookAlongIntent.GoToStep, expected), CookAlongCommands.Match($"step {word}"));

    // The digit path: "step 0" is a real target (the intro), and it must not be floored away to null.
    [Theory]
    [InlineData("step 0", 0)]
    [InlineData("step 7", 7)]
    public void A_step_digit_resolves_including_zero(string t, int expected) =>
        Assert.Equal(new CookAlongCommand(CookAlongIntent.GoToStep, expected), CookAlongCommands.Match(t));

    // "step <not-a-number>" matches the shape but resolves no number, so it is NOT a jump — it falls
    // through the grammar (here to Back, via the "step back" phrase) rather than jumping to a bogus index.
    [Fact]
    public void A_step_target_with_no_number_is_not_a_jump() =>
        Assert.Equal(CookAlongIntent.Back, Intent("step back"));

    // The two-word floor is inclusive: exactly two real words is worth waking the brain.
    [Fact]
    public void Exactly_two_words_is_worth_asking() =>
        Assert.True(CookAlongCommands.IsWorthAsking("add salt"));

    // ---- Utterance normalization (internal; exercised through Match) --------------------------------

    // Every filler word is stripped from either end, so a command wrapped in it still matches.
    [Theory]
    [InlineData("ok")] [InlineData("okay")] [InlineData("alright")] [InlineData("please")] [InlineData("now")]
    [InlineData("thanks")] [InlineData("thank")] [InlineData("you")] [InlineData("and")] [InlineData("um")]
    [InlineData("uh")] [InlineData("er")] [InlineData("hey")] [InlineData("hi")] [InlineData("so")]
    public void Filler_words_are_stripped_from_a_command(string filler) =>
        Assert.Equal(CookAlongIntent.Next, Intent($"{filler} next"));

    // Repetition collapse only fires for a unit that DIVIDES the token count: a 5-token phrase whose
    // first two tokens happen to be a command must not be truncated to that command.
    [Fact]
    public void A_non_dividing_repeat_unit_does_not_truncate() =>
        Assert.Equal(CookAlongIntent.None, Intent("next step next step twice"));

    // Repetition matches token i against token r*unit+i (forward), so a genuine end-to-end repeat of a
    // multi-word command collapses to it — a sign flip there would compare the wrong positions and miss it.
    [Fact]
    public void A_repeat_of_a_multi_word_command_collapses_to_it() =>
        Assert.Equal(CookAlongIntent.Next, Intent("i'm done with that i'm done with that"));

    // An annotation between two glued words becomes a SEPARATOR, not nothing: "go(coughing)back" is
    // "go back" (Back), not the non-word "goback".
    [Fact]
    public void An_annotation_separates_the_words_it_sits_between() =>
        Assert.Equal(CookAlongIntent.Back, Intent("go(coughing)back"));
}
