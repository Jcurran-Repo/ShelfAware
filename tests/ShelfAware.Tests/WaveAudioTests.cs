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

    // NaN has no loudness to clamp to, so it is silence. ⚠️ Nothing in the encoder MAKES that happen —
    // float.Clamp passes a NaN through and .NET's saturating float-to-integer conversion turns it into 0.
    // This test is what holds the encoder to it: an explicit NaN branch was written, found to be
    // indistinguishable from its own absence by any input, and removed, so this assertion is now the only
    // thing standing between a runtime that stopped saturating and a click in the middle of a word.
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

    // ---- Decode: the mirror, and the ear's only way in ------------------------------------------

    [Fact]
    public void A_clip_survives_the_round_trip_it_will_actually_make()
    {
        // Encode → Decode is the path a Kokoro clip takes to tools/MoonshineCheck, and the shape a
        // browser's capture arrives in. Tolerance is one 16-bit step: the container stores shorts, so
        // a float that isn't exactly on a step cannot come back bit-identical, and pretending otherwise
        // would be a test that only passes for the values someone happened to pick.
        float[] original = [0f, 0.5f, -0.5f, 1f, -1f, 0.123456f];

        var (samples, rate) = WaveAudio.Decode(WaveAudio.Encode(original, 16000));

        Assert.Equal(16000, rate);
        Assert.Equal(original.Length, samples.Length);
        for (var i = 0; i < original.Length; i++)
            Assert.True(Math.Abs(original[i] - samples[i]) < 1f / short.MaxValue,
                $"sample {i}: {original[i]} came back as {samples[i]}");
    }

    [Fact]
    public void Stereo_is_downmixed_rather_than_refused()
    {
        // A headset or a two-microphone laptop can produce it, and averaging is what a recognizer wants.
        var wav = StereoWav(16000, [(1f, 0f), (0.5f, -0.5f), (-1f, -1f)]);

        var (samples, rate) = WaveAudio.Decode(wav);

        Assert.Equal(16000, rate);
        Assert.Equal(3, samples.Length);
        Assert.True(Math.Abs(0.5f - samples[0]) < 0.001f);   // (1 + 0) / 2
        Assert.True(Math.Abs(0f - samples[1]) < 0.001f);     // (0.5 - 0.5) / 2
        Assert.True(Math.Abs(-1f - samples[2]) < 0.001f);    // both rails
    }

    [Fact]
    public void A_chunk_before_the_data_is_walked_past_rather_than_read_as_audio()
    {
        // ⚠️ Real encoders interleave LIST/fact chunks, so a reader that jumped to byte 44 would read
        // that metadata as samples — a burst of noise at the front of every clip, and to a recognizer a
        // word nobody said.
        var canonical = WaveAudio.Encode([0.25f, -0.25f], 16000);
        var withExtra = WithChunkBeforeData(canonical, "LIST", "INFOhere"u8.ToArray());

        var (samples, _) = WaveAudio.Decode(withExtra);

        Assert.Equal(2, samples.Length);
        Assert.True(Math.Abs(0.25f - samples[0]) < 0.001f);
        Assert.True(Math.Abs(-0.25f - samples[1]) < 0.001f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not audio at all")]
    public void Anything_that_is_not_a_wav_says_so_instead_of_returning_noise(string text)
    {
        // The browser falling back to webm/opus lands here. It must be a NAMED failure: silently
        // returning whatever bytes were there would transcribe compressed audio as gibberish, which
        // reads as a broken model rather than a wrong container.
        var ex = Assert.Throws<InvalidDataException>(() => WaveAudio.Decode(Encoding.UTF8.GetBytes(text)));

        Assert.Contains("Not a WAV", ex.Message);
    }

    [Fact]
    public void A_compressed_wav_is_refused_rather_than_read_as_pcm()
    {
        var wav = WaveAudio.Encode([0.25f], 16000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 2); // ADPCM, not PCM

        var ex = Assert.Throws<InvalidDataException>(() => WaveAudio.Decode(wav));

        Assert.Contains("not uncompressed PCM", ex.Message);
    }

    [Fact]
    public void An_eight_bit_wav_is_refused_rather_than_read_as_sixteen()
    {
        var wav = WaveAudio.Encode([0.25f, -0.25f], 16000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 8);

        var ex = Assert.Throws<InvalidDataException>(() => WaveAudio.Decode(wav));

        Assert.Contains("8-bit", ex.Message);
    }

    [Fact]
    public void A_truncated_clip_yields_the_samples_that_are_there()
    {
        // A recording cut off mid-upload is worth transcribing as far as it got — the words before the
        // cut are still words. What matters is that it does not throw an index out of range.
        var wav = WaveAudio.Encode([0.1f, 0.2f, 0.3f, 0.4f], 16000);
        var truncated = wav[..(wav.Length - 4)]; // two samples short of what the header claims

        var (samples, rate) = WaveAudio.Decode(truncated);

        Assert.Equal(16000, rate);
        Assert.Equal(2, samples.Length);
    }

    /// <summary>A two-channel 16-bit PCM WAV, built here rather than by the encoder — which only writes
    /// mono, and is the thing under test.</summary>
    private static byte[] StereoWav(int sampleRate, (float Left, float Right)[] frames)
    {
        var data = new byte[frames.Length * 4];
        for (var i = 0; i < frames.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4), (short)(frames[i].Left * short.MaxValue));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4 + 2), (short)(frames[i].Right * short.MaxValue));
        }

        var wav = new byte[44 + data.Length];
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 2);              // channels
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 4);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data.Length);
        data.CopyTo(span[44..]);
        return wav;
    }

    /// <summary>The same WAV with an extra chunk spliced in between "fmt " and "data".</summary>
    private static byte[] WithChunkBeforeData(byte[] canonical, string id, byte[] body)
    {
        var head = canonical[..36];                       // through the fmt chunk
        var tail = canonical[36..];                       // "data" onwards
        var chunk = new byte[8 + body.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk.AsSpan());
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), body.Length);
        body.CopyTo(chunk.AsSpan(8));

        var result = new byte[head.Length + chunk.Length + tail.Length];
        head.CopyTo(result.AsSpan());
        chunk.CopyTo(result.AsSpan(head.Length));
        tail.CopyTo(result.AsSpan(head.Length + chunk.Length));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        return result;
    }

}
