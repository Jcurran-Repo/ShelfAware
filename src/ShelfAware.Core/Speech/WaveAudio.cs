namespace ShelfAware.Core.Speech;

/// <summary>
/// Packs raw PCM samples into a WAV container.
///
/// <para>An in-process synthesizer hands back float samples and a sample rate — not a file. Something has
/// to make those into bytes a browser will play, and WAV is the honest answer: the container is a 44-byte
/// header over the samples themselves, so nothing is re-encoded, nothing is lost, and there is no codec
/// to take a dependency on. The cost is size — 16-bit mono at 24 kHz is ~48 KB per spoken second, where
/// MP3 would be a tenth of that — which is a real consideration for <c>Speech:CacheMegabytes</c> and is
/// written down in <c>docs/deploy-kokoro.md</c> rather than guessed at here.</para>
///
/// <para>⚠️ Samples are CLAMPED, not scaled. A synthesizer can return a value just outside ±1, and
/// multiplying that by 32767 and casting wraps a loud sample to the opposite extreme — a click in the
/// middle of a word. Clamping costs one comparison per sample and cannot produce that.</para>
/// </summary>
public static class WaveAudio
{
    /// <summary>What <see cref="Encode"/> produces, for anything describing the bytes without opening them.</summary>
    public const string MediaType = "audio/wav";

    private const int HeaderBytes = 44;
    private const short PcmFormat = 1;
    private const short BitsPerSample = 16;
    private const short Channels = 1;


    /// <summary>
    /// Encodes mono float samples (nominally −1…1) as 16-bit PCM in a WAV container.
    /// </summary>
    /// <param name="samples">The samples, in order. An empty span is a valid — if silent — clip.</param>
    /// <param name="sampleRate">Samples per second, as reported by whatever produced them. Must be positive:
    /// a zero or negative rate would write a header that says the clip has no duration, which players
    /// variously reject or render as noise.</param>
    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        var dataBytes = samples.Length * sizeof(short);

        // ⚠️ A local with an explicit cast, not `const short` and not inline arithmetic — both of those
        // compile only because the compiler folds them to a constant, and a mutation testing tool rewrites
        // the expression into a ternary, which is not one. That made every mutant in this method fail to
        // compile and be discarded, so the mutation gate passed over this file having tested NOTHING: 34
        // mutants, 34 compile errors, one green check. The cast keeps the arithmetic visible and keeps it
        // mutable, which is the only way the gate can actually judge it.
        var blockAlign = (short)(Channels * BitsPerSample / 8);
        var wav = new byte[HeaderBytes + dataBytes];
        var span = wav.AsSpan();

        // RIFF header. Sizes are "everything after this field", which is why the first is total − 8.
        "RIFF"u8.CopyTo(span);
        WriteInt32(span[4..], wav.Length - 8);
        "WAVE"u8.CopyTo(span[8..]);

        // "fmt " chunk: 16 bytes of PCM format description.
        "fmt "u8.CopyTo(span[12..]);
        WriteInt32(span[16..], 16);
        WriteInt16(span[20..], PcmFormat);
        WriteInt16(span[22..], Channels);
        WriteInt32(span[24..], sampleRate);
        WriteInt32(span[28..], sampleRate * blockAlign);                   // byte rate
        WriteInt16(span[32..], blockAlign);                                // block align
        WriteInt16(span[34..], BitsPerSample);

        // "data" chunk: the samples.
        "data"u8.CopyTo(span[36..]);
        WriteInt32(span[40..], dataBytes);

        for (var i = 0; i < samples.Length; i++)
        {
            // ⚠️ Clamp, don't scale (see the class remark). There is deliberately no NaN branch:
            // float.Clamp passes a NaN through and .NET's float-to-integer conversion is saturating, so a
            // NaN becomes 0 — silence — on its own. An explicit `IsNaN(x) ? 0f : …` was written here first
            // and removed: no input could tell the two apart, so it was a comparison per sample buying a
            // guarantee the runtime already makes. The guarantee is pinned by a test instead
            // (A_sample_that_is_not_a_number_is_silence), which is the thing that would notice if it ever
            // stopped being true.
            var sample = float.Clamp(samples[i], -1f, 1f);
            WriteInt16(span[(HeaderBytes + i * sizeof(short))..], (short)(sample * short.MaxValue));
        }

        return wav;
    }

    private static void WriteInt32(Span<byte> destination, int value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination, value);

    private static void WriteInt16(Span<byte> destination, short value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(destination, value);
}
