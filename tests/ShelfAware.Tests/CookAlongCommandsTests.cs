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
    public void Next_phrases_advance(string t) => Assert.Equal(CookAlongIntent.Next, CookAlongCommands.Match(t));

    [Theory]
    [InlineData("back")]
    [InlineData("go back")]
    [InlineData("previous step")]
    [InlineData("Back up.")]
    [InlineData("one step back")]
    public void Back_phrases_go_back(string t) => Assert.Equal(CookAlongIntent.Back, CookAlongCommands.Match(t));

    [Theory]
    [InlineData("repeat")]
    [InlineData("say that again")]
    [InlineData("one more time")]
    [InlineData("What was that?")]
    [InlineData("come again")]
    public void Repeat_phrases_repeat(string t) => Assert.Equal(CookAlongIntent.Repeat, CookAlongCommands.Match(t));

    [Theory]
    [InlineData("hold on")]
    [InlineData("wait")]
    [InlineData("hang on")]
    [InlineData("just a sec")]
    [InlineData("give me a minute")]
    [InlineData("pause")]
    public void Hold_phrases_hold(string t) => Assert.Equal(CookAlongIntent.Hold, CookAlongCommands.Match(t));

    [Theory]
    [InlineData("i'm back")]
    [InlineData("ready")]
    [InlineData("resume")]
    [InlineData("let's go")]
    public void Resume_phrases_resume(string t) => Assert.Equal(CookAlongIntent.Resume, CookAlongCommands.Match(t));

    [Theory]
    [InlineData("stop reading")]
    [InlineData("stop cooking")]
    [InlineData("never mind")]
    [InlineData("I'm done cooking.")]
    [InlineData("stop listening")]   // the general session-stop grammar counts here too
    [InlineData("goodbye")]
    [InlineData("that's all")]
    public void Stop_phrases_end_the_cook_along(string t) => Assert.Equal(CookAlongIntent.Stop, CookAlongCommands.Match(t));

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
        Assert.Equal(CookAlongIntent.None, CookAlongCommands.Match(t));

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
        Assert.Equal(CookAlongIntent.None, CookAlongCommands.Match(t));

    // Stop wins over everything: an utterance that ends the session must never be read as navigation.
    [Fact]
    public void Stop_is_matched_before_the_navigation_grammar() =>
        Assert.Equal(CookAlongIntent.Stop, CookAlongCommands.Match("stop the cook along"));

    [Theory]
    [InlineData("can I use butter instead", true)]
    [InlineData("how much salt", true)]
    [InlineData("mm", false)]         // a stray syllable off the extractor fan
    [InlineData("uh", false)]
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
