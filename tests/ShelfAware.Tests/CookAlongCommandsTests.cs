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
}
