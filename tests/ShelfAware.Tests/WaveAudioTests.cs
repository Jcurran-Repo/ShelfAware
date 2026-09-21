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

    // ---- Decode: what a client can actually send, hostile shapes included ------------------------
    //
    // ⚠️ These exist because the pre-merge mutation gate found nineteen survivors in Decode and two
    // malformed inputs escaped as the WRONG EXCEPTION TYPE (2026-09-21). Decode reads bytes that come
    // from a browser — which is to say from whoever is holding the browser — and its only caller,
    // MoonshineSpeechToText, catches InvalidDataException and nothing else. An ArgumentOutOfRangeException
    // out of here is not a parse failure, it is a Blazor circuit torn down with "An unhandled error has
    // occurred", from a page with no ErrorBoundary behind it. So every refusal below asserts the TYPE as
    // much as the sentence.

    [Fact]
    public void A_header_with_no_samples_after_it_is_an_empty_clip_rather_than_a_refusal()
    {
        // Exactly 44 bytes: the header's own length, and a data chunk whose 8-byte header ends on the
        // last byte of the buffer. Both boundaries in one clip — the minimum-length guard must admit it,
        // and the chunk walk must still look at a header that only just fits.
        var wav = WaveAudio.Encode([], 16000);
        Assert.Equal(44, wav.Length);

        var (samples, rate) = WaveAudio.Decode(wav);

        Assert.Empty(samples);
        Assert.Equal(16000, rate);
    }

    [Theory]
    [InlineData("RIFX", "WAVE")]
    [InlineData("RIFF", "AVI ")]
    public void Either_half_of_the_riff_marker_being_wrong_is_a_refusal(string riff, string wave)
    {
        // Both halves, separately: a reader that asked for RIFF *and* WAVE with an && would accept a
        // file that had one of them, and go on to walk chunks that aren't chunks.
        var wav = WaveAudio.Encode([0.25f], 16000);
        Encoding.ASCII.GetBytes(riff).CopyTo(wav.AsSpan());
        Encoding.ASCII.GetBytes(wave).CopyTo(wav.AsSpan(8));

        var ex = Assert.Throws<InvalidDataException>(() => WaveAudio.Decode(wav));

        Assert.Contains("no RIFF/WAVE marker", ex.Message);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 19)]
    [InlineData(-1)]
    public void A_chunk_size_that_cannot_be_true_is_a_named_refusal_not_an_index_error(int declared)
    {
        // ⚠️ int.MaxValue is the one that mattered: `at + 8 + size` overflowed to a negative, which is
        // not greater than the buffer length, so the clamp let it through and the Slice below threw
        // ArgumentOutOfRangeException — out through TranscribeAsync, out through an @onclick handler.
        var wav = Riff(Lying("JUNK", declared, []), Fmt(1, 1, 16000, 16), Chunk("data", Sixteens(0.25f)));

        var ex = Record.Exception(() => WaveAudio.Decode(wav));

        Assert.IsType<InvalidDataException>(ex);   // ← the type IS the assertion here
        Assert.Contains("Not a WAV", ex.Message);
    }

    [Theory]
    [InlineData((short)0, "0 channel")]
    [InlineData((short)-1, "-1 channel")]
    [InlineData((short)17, "17 channel")]
    public void A_channel_count_no_microphone_has_is_a_named_refusal(short channels, string says)
    {
        // 0xFFFF in the file reads back as -1, because the field is signed — and a negative divisor made
        // `frames` negative, which made `new float[frames]` throw OverflowException. 17 is one past what
        // this will downmix; zero used to be reported as "data before fmt", which named the wrong fault.
        var wav = Riff(Fmt(1, channels, 16000, 16), Chunk("data", Sixteens(0.25f, -0.25f)));

        var ex = Record.Exception(() => WaveAudio.Decode(wav));

        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains(says, ex.Message);
    }

    [Fact]
    public void The_most_channels_it_will_downmix_are_still_downmixed()
    {
        // The other side of the bound: sixteen is in, and a test that only pinned the refusal would let
        // someone tighten the limit to two and break a microphone array with a green suite.
        var frame = new float[16];
        Array.Fill(frame, 0.5f);
        var wav = Riff(Fmt(1, 16, 16000, 16), Chunk("data", Sixteens(frame)));

        var (samples, _) = WaveAudio.Decode(wav);

        Assert.Single(samples);
        Assert.True(Math.Abs(0.5f - samples[0]) < 0.001f);
    }

    [Fact]
    public void A_fmt_chunk_too_short_to_hold_a_format_is_a_named_refusal()
    {
        var wav = Riff(Chunk("fmt ", new byte[14]), Chunk("data", Sixteens(0.25f)));

        var ex = Assert.Throws<InvalidDataException>(() => WaveAudio.Decode(wav));

        Assert.Contains("fmt chunk is too short", ex.Message);
    }

    [Fact]
    public void An_extensible_fmt_chunk_is_read_as_the_pcm_it_is()
    {
        // ⚠️ This is what a browser's OfflineAudioContext export looks like — WAVE_FORMAT_EXTENSIBLE
        // (0xFFFE) with an 18-byte fmt chunk — so it is the shape pcm.js can genuinely produce, and it
        // was the one format the comment named and no test covered.
        var wav = Riff(Fmt(unchecked((short)0xFFFE), 1, 16000, 16, extraBytes: 2), Chunk("data", Sixteens(0.5f, -0.5f)));

        var (samples, rate) = WaveAudio.Decode(wav);

        Assert.Equal(16000, rate);
        Assert.Equal(2, samples.Length);
        Assert.True(Math.Abs(0.5f - samples[0]) < 0.001f);
    }

    [Fact]
    public void Data_before_fmt_is_a_named_refusal()
    {
        var wav = Riff(Chunk("data", Sixteens(0.25f)), Fmt(1, 1, 16000, 16));

        var ex = Assert.Throws<InvalidDataException>(() => WaveAudio.Decode(wav));

        Assert.Contains("data before fmt", ex.Message);
    }

    [Fact]
    public void A_wav_with_no_data_chunk_at_all_is_a_named_refusal()
    {
        var wav = Riff(Fmt(1, 1, 16000, 16), Chunk("LIST", "INFOhere"u8.ToArray()));

        var ex = Record.Exception(() => WaveAudio.Decode(wav));

        Assert.IsType<InvalidDataException>(ex);   // walking off the end must not index past the buffer
        Assert.Contains("no data chunk", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-8000)]
    public void A_header_that_claims_an_impossible_sample_rate_is_a_named_refusal(int rate)
    {
        // Zero is the one that matters: a rate of zero divides into the duration the log line reports,
        // and a `< 0` guard would wave it through.
        var wav = Riff(Fmt(1, 1, rate, 16), Chunk("data", Sixteens(0.25f)));

        var ex = Assert.Throws<InvalidDataException>(() => WaveAudio.Decode(wav));

        Assert.Contains($"{rate} Hz", ex.Message);
    }

    [Fact]
    public void An_odd_sized_chunk_is_walked_past_with_its_pad_byte()
    {
        // ⚠️ RIFF chunks are word-aligned: an odd body carries a pad byte the size field doesn't count.
        // A reader that forgets it lands one byte inside "data" and never finds the samples — and every
        // encoder that writes an odd-length LIST produces exactly this.
        var wav = Riff(Chunk("LIST", "INFOodd"u8.ToArray()), Fmt(1, 1, 16000, 16), Chunk("data", Sixteens(0.25f, -0.25f)));

        var (samples, _) = WaveAudio.Decode(wav);

        Assert.Equal(2, samples.Length);
        Assert.True(Math.Abs(0.25f - samples[0]) < 0.001f);
    }

    [Fact]
    public void An_empty_chunk_is_walked_past_rather_than_swallowing_the_rest_of_the_file()
    {
        // A zero-size chunk is legal and real (an empty LIST). Treated as "truncated, take what's
        // there" it would eat the fmt and data chunks behind it and the clip would vanish.
        var wav = Riff(Chunk("LIST", []), Fmt(1, 1, 16000, 16), Chunk("data", Sixteens(0.25f, -0.25f)));

        var (samples, _) = WaveAudio.Decode(wav);

        Assert.Equal(2, samples.Length);
    }

    // ---- Builders for the shapes above, none of which the encoder can produce ---------------------

    /// <summary>A RIFF/WAVE file wrapping the chunks given, in the order given.</summary>
    private static byte[] Riff(params byte[][] chunks)
    {
        var body = chunks.Sum(c => c.Length);
        var wav = new byte[12 + body];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav.AsSpan());
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav.AsSpan(8));
        var at = 12;
        foreach (var chunk in chunks) { chunk.CopyTo(wav.AsSpan(at)); at += chunk.Length; }
        return wav;
    }

    /// <summary>One chunk, honestly sized, padded to an even length as the format requires.</summary>
    private static byte[] Chunk(string id, byte[] body)
    {
        var chunk = new byte[8 + body.Length + body.Length % 2];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk.AsSpan());
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), body.Length);
        body.CopyTo(chunk.AsSpan(8));
        return chunk;
    }

    /// <summary>One chunk whose size field says something the bytes do not — which is all a client has
    /// to do to get here.</summary>
    private static byte[] Lying(string id, int declaredSize, byte[] body)
    {
        var chunk = Chunk(id, body);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), declaredSize);
        return chunk;
    }

    private static byte[] Fmt(short format, short channels, int sampleRate, short bits, int extraBytes = 0)
    {
        var body = new byte[16 + extraBytes];
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(0), format);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), sampleRate * Math.Max(channels, (short)1) * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(12), (short)(Math.Max(channels, (short)1) * bits / 8));
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(14), bits);
        return Chunk("fmt ", body);
    }

    /// <summary>Samples as the 16-bit little-endian shorts a data chunk holds.</summary>
    private static byte[] Sixteens(params float[] samples)
    {
        var data = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * sizeof(short)), (short)(samples[i] * short.MaxValue));
        return data;
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
