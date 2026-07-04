using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

public class VoiceCommandsTests
{
    [Theory]
    [InlineData("stop listening")]
    [InlineData("Stop listening.")]
    [InlineData("Okay, stop listening")]
    [InlineData("please stop listening now")]
    [InlineData("STOP THE CONVERSATION")]
    [InlineData("end the conversation, thanks")]
    [InlineData("goodbye")]
    [InlineData("That's all.")]
    [InlineData("we're done")]
    public void Stop_phrases_end_the_session(string transcript) =>
        Assert.True(VoiceCommands.IsStop(transcript));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("don't stop listening")]
    [InlineData("stop")]                                        // bare "stop" is too ambiguous mid-conversation
    [InlineData("we're out of milk, then stop listening")]     // statement first — goes to the model, not dropped
    [InlineData("add goodbye cookies to the list")]
    [InlineData("what am I low on")]
    public void Ordinary_speech_keeps_the_session_open(string? transcript) =>
        Assert.False(VoiceCommands.IsStop(transcript));
}
