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

    /// <summary>The most channels <see cref="Decode"/> will downmix. A microphone array is the most a
    /// browser can plausibly hand over; past that the header is describing something this app did not
    /// record, and refusing is cheaper than averaging it.</summary>
    private const int MaxChannels = 16;


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


    /// <summary>
    /// Reads a WAV back into mono float samples — the mirror of <see cref="Encode"/>, and here beside it
    /// for that reason: two places that disagree about what a WAV is would disagree silently, one writing
    /// a header the other misreads.
    ///
    /// <para>This exists because an in-process RECOGNIZER is the reverse of an in-process synthesizer: it
    /// wants samples, and what arrives from a browser is a container. Decoding a COMPRESSED container
    /// (the webm/opus a MediaRecorder produces) would mean a codec dependency — ffmpeg or similar — and
    /// that would throw away the property that makes the in-process shape worth having: nothing to install
    /// beside the app. So the browser hands over PCM, and this reads it.</para>
    ///
    /// <para>Stereo is downmixed rather than refused: a headset or a laptop with two microphones can
    /// produce it, and averaging the channels is what every recognizer wants anyway. The sample RATE is
    /// passed back rather than converted — resampling belongs where the audio is captured, and a caller
    /// that needs a particular rate should say so about a number it can see.</para>
    /// </summary>
    /// <returns>The mono samples and their rate.</returns>
    /// <exception cref="InvalidDataException">If the bytes are not a PCM WAV this can read. The message
    /// names what was wrong, because the one thing a person needs to know here is whether the browser sent
    /// the wrong SHAPE or nothing at all.</exception>
    public static (float[] Samples, int SampleRate) Decode(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < HeaderBytes) throw new InvalidDataException(
            $"Not a WAV: {wav.Length} byte(s), which is shorter than a WAV header.");
        if (!wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a WAV: no RIFF/WAVE marker. Compressed audio is not read here.");

        int channels = 0, sampleRate = 0, bits = 0;
        var format = (short)0;
        var sawFmt = false;

        // Walk the chunks rather than assuming the canonical 44-byte layout: real encoders interleave
        // LIST/fact chunks before the data, and a reader that jumped to byte 44 would read those as audio.
        var at = 12;
        while (at + 8 <= wav.Length)
        {
            var id = wav.Slice(at, 4);
            var size = ReadInt32(wav[(at + 4)..]);
            // ⚠️ Written as a SUBTRACTION, never `at + 8 + size > wav.Length`: the size is four bytes of
            // whatever the client sent, and an addition overflows to a negative on a declared size near
            // int.MaxValue — which passes both halves of this guard and leaves the Slice below to throw
            // ArgumentOutOfRangeException, an exception this method's contract does not allow and its
            // only caller does not catch. The loop condition guarantees the right-hand side is >= 0.
            // Stryker disable once Equality: `>` and `>=` are equivalent on the second comparison — at
            // exactly `wav.Length - at - 8` the clamp assigns `size` the value it already holds, so no
            // input can tell the two readings apart. (`size < 0` on the left is a different question and
            // is tested.) A test could not kill this one; only a claim that it cannot be killed can.
            if (size < 0 || size > wav.Length - at - 8) size = wav.Length - at - 8; // truncated: take what's there
            var body = wav.Slice(at + 8, size);

            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16) throw new InvalidDataException("Not a WAV this can read: its fmt chunk is too short.");
                format = ReadInt16(body);
                channels = ReadInt16(body[2..]);
                sampleRate = ReadInt32(body[4..]);
                bits = ReadInt16(body[14..]);
                sawFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!sawFmt) throw new InvalidDataException("Not a WAV this can read: data before fmt.");
                // ⚠️ `<= 0`, not `== 0`: the channel count is a SIGNED 16-bit field, so 0xFFFF reads as
                // -1, and a negative divisor below makes `frames` negative and `new float[frames]` throw
                // OverflowException — again an exception this contract does not allow. A count is also
                // bounded: a capture with more channels than this is a header that is lying about
                // something, and downmixing a thousand of them is work nobody asked for.
                if (channels is <= 0 or > MaxChannels) throw new InvalidDataException(
                    $"Not a WAV this can read: its header says {channels} channel(s).");
                // 1 = PCM. 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose samples are still PCM when the bit
                // depth says 16 — which is what a browser's OfflineAudioContext export looks like.
                if (format is not (PcmFormat or unchecked((short)0xFFFE)))
                    throw new InvalidDataException(
                        $"Not a WAV this can read: format {format} is not uncompressed PCM.");
                if (bits != BitsPerSample) throw new InvalidDataException(
                    $"Not a WAV this can read: {bits}-bit samples, expected {BitsPerSample}-bit.");
                if (sampleRate <= 0) throw new InvalidDataException(
                    $"Not a WAV this can read: its header says {sampleRate} Hz.");

                var frames = body.Length / sizeof(short) / channels;
                var samples = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    // Downmix by averaging, in float so a loud stereo pair can't wrap on the way to mono.
                    var sum = 0f;
                    for (var c = 0; c < channels; c++)
                        sum += ReadInt16(body[((i * channels + c) * sizeof(short))..]) / 32768f;
                    samples[i] = sum / channels;
                }

                return (samples, sampleRate);
            }

            at += 8 + size + (size % 2); // chunks are word-aligned; an odd size carries a pad byte
        }

        throw new InvalidDataException("Not a WAV this can read: no data chunk.");
    }

    private static int ReadInt32(ReadOnlySpan<byte> source) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(source);

    private static short ReadInt16(ReadOnlySpan<byte> source) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(source);

    private static void WriteInt32(Span<byte> destination, int value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination, value);

    private static void WriteInt16(Span<byte> destination, short value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(destination, value);
}
