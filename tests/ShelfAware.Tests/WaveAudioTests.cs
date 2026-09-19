using System.Buffers.Binary;
using System.Text;
using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

/// <summary>
/// The WAV container an in-process synthesizer's samples are handed to the browser in. These are the
/// bytes a player parses, so the tests read them back as a player would rather than comparing against a
/// blob recorded from the encoder itself — a golden-bytes test would pass just as happily on a header
/// that is wrong in the same way twice.
/// </summary>
public class WaveAudioTests
{
    private static readonly float[] Samples = [0f, 1f, -1f, 0.5f];

    [Fact]
    public void A_clip_announces_itself_as_16_bit_mono_pcm_at_the_rate_it_was_sampled()
    {
        var wav = WaveAudio.Encode(Samples, 24000);

        Assert.Equal("RIFF", Ascii(wav, 0));
        Assert.Equal("WAVE", Ascii(wav, 8));
        Assert.Equal("fmt ", Ascii(wav, 12));
        Assert.Equal(16, Int32At(wav, 16));       // fmt chunk length
        Assert.Equal(1, Int16At(wav, 20));        // PCM, uncompressed
        Assert.Equal(1, Int16At(wav, 22));        // mono
        Assert.Equal(24000, Int32At(wav, 24));    // sample rate
        Assert.Equal(48000, Int32At(wav, 28));    // byte rate = rate * channels * bytes per sample
        Assert.Equal(2, Int16At(wav, 32));        // block align
        Assert.Equal(16, Int16At(wav, 34));       // bits per sample
        Assert.Equal("data", Ascii(wav, 36));
    }

    // ⚠️ The two length fields are the ones a player trusts over the file it actually got. A RIFF size
    // that over-reports reads past the end; a data size that under-reports truncates the last words of a
    // recipe step, silently.
    [Fact]
    public void The_declared_lengths_match_the_bytes_that_are_really_there()
    {
        var wav = WaveAudio.Encode(Samples, 24000);

        Assert.Equal(44 + Samples.Length * 2, wav.Length);
        Assert.Equal(wav.Length - 8, Int32At(wav, 4));
        Assert.Equal(Samples.Length * 2, Int32At(wav, 40));
    }

    [Fact]
    public void Samples_survive_the_round_trip_at_full_scale()
    {
        var wav = WaveAudio.Encode(Samples, 24000);

        Assert.Equal(0, Int16At(wav, 44));
        Assert.Equal(short.MaxValue, Int16At(wav, 46));
        Assert.Equal(-short.MaxValue, Int16At(wav, 48));
        Assert.Equal(short.MaxValue / 2, Int16At(wav, 50));
    }

    // ⚠️ The reason clamping is in the encoder rather than assumed of the caller: a model can return a
    // value a hair outside ±1, and `(short)(1.0001f * 32767)` wraps to a full-scale sample of the OPPOSITE
    // sign — an audible click in the middle of a word, on the loudest part of the sentence.
    [Theory]
    [InlineData(1.5f, short.MaxValue)]
    [InlineData(-1.5f, -short.MaxValue)]
    [InlineData(float.PositiveInfinity, short.MaxValue)]
    [InlineData(float.NegativeInfinity, -short.MaxValue)]
    public void A_sample_outside_full_scale_is_clamped_rather_than_wrapped(float sample, short expected) =>
        Assert.Equal(expected, Int16At(WaveAudio.Encode([sample], 24000), 44));

    // NaN has no loudness to clamp to, so it is silence — the one value where "do the arithmetic anyway"
    // has no defined answer at all.
    [Fact]
    public void A_sample_that_is_not_a_number_is_silence() =>
        Assert.Equal(0, Int16At(WaveAudio.Encode([float.NaN], 24000), 44));

    // A synthesis can legitimately produce nothing; the result still has to be a file a player accepts.
    [Fact]
    public void An_empty_clip_is_still_a_valid_wav_file()
    {
        var wav = WaveAudio.Encode([], 24000);

        Assert.Equal(44, wav.Length);
        Assert.Equal(0, Int32At(wav, 40));
        Assert.Equal(36, Int32At(wav, 4));
    }

    // A header claiming zero samples per second describes a clip with no duration — players variously
    // reject it or render noise. Refuse it here, where the caller can still see which number was wrong.
    [Theory]
    [InlineData(0)]
    [InlineData(-24000)]
    public void A_rate_that_cannot_describe_a_clip_is_refused(int sampleRate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveAudio.Encode(Samples, sampleRate));

    private static string Ascii(byte[] wav, int offset) => Encoding.ASCII.GetString(wav, offset, 4);

    private static int Int32At(byte[] wav, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset));

    private static short Int16At(byte[] wav, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(offset));
}
