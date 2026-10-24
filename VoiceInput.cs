using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;
using Whisper.net;

namespace AlexaWidget;

/// <summary>
/// How Whisper is run on an utterance; the voice benchmark compares these and the app uses <see cref="Fast"/>
/// (~7× faster than <see cref="Original"/> on a 6-core CPU, with the same text on the benchmark commands).
/// </summary>
sealed record WhisperTuning(string Name, bool Trim, bool ShortContext)
{
    public static readonly WhisperTuning Original = new("original", Trim: false, ShortContext: false);
    public static readonly WhisperTuning Fast = new("rápido", Trim: true, ShortContext: true);
}

/// <summary>
/// Records one utterance from the default microphone (stops on silence) and transcribes it offline with Whisper.
/// The model is downloaded once to %LOCALAPPDATA%\AlexaWidget\models.
/// </summary>
sealed partial class VoiceInput(string dataDir, string language, string? modelFile = null)
{
    const string ModelName = "ggml-small-q5_1.bin";
    const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelName;
    // SHA-256 of the file above (its Hugging Face LFS pointer); a corrupt or altered download is discarded.
    const string ModelSha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";
    public const int SampleRate = 16000;
    static readonly TimeSpan MaxUtterance = TimeSpan.FromSeconds(12);
    static readonly TimeSpan WaitForSpeech = TimeSpan.FromSeconds(6);
    static readonly TimeSpan TrailingSilence = TimeSpan.FromMilliseconds(900);

    // modelFile: another ggml model already on disk, for comparing models in the voice benchmark.
    readonly string modelPath = modelFile ?? Path.Combine(dataDir, "models", ModelName);
    readonly SemaphoreSlim busy = new(1, 1);
    readonly SemaphoreSlim download = new(1, 1);
    readonly Lock factoryLock = new();
    Task<WhisperFactory>? factory;
    CancellationTokenSource? session;

    /// <summary>Diagnostics hook: receives a line per audio chunk (level vs. threshold).</summary>
    public Action<string>? Trace { get; set; }

    public WhisperTuning Tuning { get; set; } = WhisperTuning.Fast;

    /// <summary>Starts loading an already-downloaded model in the background so commands don't wait for it.</summary>
    public void Preload()
    {
        if (!File.Exists(modelPath)) return;
        Trace?.Invoke("model: preload start");
        LoadFactory().ContinueWith(t => Trace?.Invoke($"model: preload {t.Status}"));
    }

    /// <summary>
    /// Ends the current listen at any stage. Before recording starts it cancels it; during recording it stops
    /// and still transcribes what was said.
    /// </summary>
    public void Stop()
    {
        Trace?.Invoke(session is null ? "stop: no active session" : "stop: cancelling session");
        session?.Cancel();
    }

    /// <param name="status">"downloading" (with percent), "listening" or "transcribing".</param>
    public async Task<string> ListenAsync(Action<string, int?> status)
    {
        if (!await busy.WaitAsync(0))
        {
            Stop();
            return "";
        }
        // Created before anything slow so a stop pressed while the mic is still opening is not lost.
        using var stop = new CancellationTokenSource();
        session = stop;
        Trace?.Invoke("listen: session open");
        try
        {
            await EnsureModelAsync(status);
            var model = LoadFactory(); // loads in parallel with the recording
            if (stop.IsCancellationRequested) return "";
            var recording = await RecordAsync(stop.Token, () =>
            {
                Trace?.Invoke("listen: capturing");
                status("listening", null);
            });
            var samples = Tuning.Trim ? recording.Speech : recording.All;
            Trace?.Invoke($"listen: {samples.Length / (double)SampleRate:F1} s to transcribe (stopped by user: {stop.IsCancellationRequested})");
            if (samples.Length < SampleRate / 4) return "";
            status("transcribing", null);
            var factoryReady = await model;
            Trace?.Invoke("listen: model ready");
            var raw = await Task.Run(() => TranscribeAsync(factoryReady, samples, Tuning));
            var text = Fix(Clean(raw));
            if (text.Length == 0 && Tuning != WhisperTuning.Original)
            {
                // Something was said but the fast pass found nothing: worth the slower original one, on the whole
                // recording (a trimmed clip with the full context is where Whisper makes things up).
                Trace?.Invoke($"listen: empty fast pass (raw \"{raw.Trim()}\"), retrying the original way");
                raw = await Task.Run(() => TranscribeAsync(factoryReady, recording.All, WhisperTuning.Original));
                text = Fix(Clean(raw));
            }
            Trace?.Invoke($"listen: transcribed \"{text}\" (raw \"{raw.Trim()}\")");
            return text;
        }
        finally
        {
            session = null;
            busy.Release();
            Trace?.Invoke("listen: session closed");
        }
    }

    Task<WhisperFactory> LoadFactory()
    {
        lock (factoryLock)
        {
            if (factory is null || factory.IsFaulted)
                factory = Task.Run(() => WhisperFactory.FromPath(modelPath));
            return factory;
        }
    }

    /// <summary>Diagnostics: transcribes a 16 kHz mono WAV file.</summary>
    public async Task<string> TranscribeFileAsync(string wavPath, Action<string, int?> status, WhisperTuning? tuning = null)
    {
        await EnsureModelAsync(status);
        tuning ??= Tuning;
        var samples = ReadWav(wavPath);
        if (tuning.Trim) samples = VoiceActivity.TrimToSpeech(samples);
        return Fix(Clean(await TranscribeAsync(await LoadFactory(), samples, tuning)));
    }

    /// <summary>Diagnostics: records one utterance the way the app does and saves the whole of it as a 16 kHz WAV.</summary>
    public async Task<bool> RecordSampleAsync(string wavPath)
    {
        var recording = await RecordAsync(CancellationToken.None, () => Trace?.Invoke("record: speak now"));
        if (recording.All.Length == 0) return false;
        using var writer = new WaveFileWriter(wavPath, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
        writer.WriteSamples(recording.All, 0, recording.All.Length);
        return true;
    }

    public static float[] ReadWav(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        var provider = reader.ToSampleProvider();
        var samples = new float[reader.SampleCount];
        var read = provider.Read(samples, 0, samples.Length);
        return samples[..read];
    }

    async Task EnsureModelAsync(Action<string, int?> status)
    {
        if (File.Exists(modelPath)) return;
        await download.WaitAsync(); // two listens (or a listen and a diagnostic) must not download it twice
        try
        {
            if (!File.Exists(modelPath)) await DownloadModelAsync(status);
        }
        finally
        {
            download.Release();
        }
    }

    async Task DownloadModelAsync(Action<string, int?> status)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        var partial = modelPath + ".part";
        status("downloading", 0);

        using var http = new HttpClient();
        using var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await resp.Content.ReadAsStreamAsync())
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long read = 0;
            var lastPercent = 0;
            int n;
            while ((n = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n));
                sha.AppendData(buffer, 0, n);
                read += n;
                var percent = total > 0 ? (int)(read * 100 / total) : 0;
                if (percent != lastPercent) status("downloading", lastPercent = percent);
            }
        }
        if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new AlexaException("O modelo de voz baixado veio corrompido. Tente de novo.");
        }
        File.Move(partial, modelPath, overwrite: true);
    }

    /// <param name="All">Everything captured.</param>
    /// <param name="Speech">Only the voiced part, with a small margin.</param>
    sealed record Recording(float[] All, float[] Speech)
    {
        public static readonly Recording Empty = new([], []);
    }

    /// <summary>Records until the speaker pauses; empty when nothing was said.</summary>
    async Task<Recording> RecordAsync(CancellationToken userStop, Action onStarted)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(userStop);
        stop.CancelAfter(MaxUtterance);
        var samples = new List<float>(SampleRate * (int)MaxUtterance.TotalSeconds);
        var stopped = new TaskCompletionSource();
        var started = DateTime.UtcNow;
        var lastVoice = started;
        var vad = new VoiceActivity();

        using var input = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
        input.DataAvailable += (_, e) =>
        {
            var count = e.BytesRecorded / 2;
            var chunk = new float[count];
            for (var i = 0; i < count; i++) chunk[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            var offset = samples.Count;
            samples.AddRange(chunk);

            var now = DateTime.UtcNow;
            if (vad.Feed(chunk, offset)) lastVoice = now;
            if (vad.Chunks % 10 == 0) Trace?.Invoke(vad.Describe());
            if (vad.Heard ? now - lastVoice > TrailingSilence : now - started > WaitForSpeech)
                stop.Cancel(); // safe from the capture thread: the registration below only calls StopRecording
        };
        input.RecordingStopped += (_, e) =>
        {
            if (e.Exception is { } ex) stopped.TrySetException(ex);
            else stopped.TrySetResult();
        };

        if (stop.IsCancellationRequested) return Recording.Empty;
        try
        {
            Trace?.Invoke("record: opening microphone");
            input.StartRecording();
            Trace?.Invoke("record: microphone open");
        }
        catch (NAudio.MmException)
        {
            throw new AlexaException("Nenhum microfone disponível. Confira o microfone padrão do Windows.");
        }
        // Registered only once capturing: a stop that already happened runs StopRecording right here.
        using (stop.Token.Register(input.StopRecording))
        {
            onStarted();
            await stopped.Task;
        }
        Trace?.Invoke($"record: {samples.Count / (double)SampleRate:F1} s captured, {vad.Describe()}");
        // Stopped by hand before the level ever rose: still try, a very quiet microphone may have caught the command.
        if (!vad.Heard && !userStop.IsCancellationRequested) return Recording.Empty;
        float[] all = [.. samples];
        return new Recording(all, vad.Trim(all));
    }

    public async Task<string> TranscribeAsync(WhisperFactory whisper, float[] samples, WhisperTuning tuning)
    {
        samples = PadToMinimum(samples);
        var builder = whisper.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Max(2, Environment.ProcessorCount / 2));
        if (tuning.ShortContext) builder = builder.WithAudioContextSize(AudioContextFor(samples.Length));
        await using var processor = builder.Build();
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples))
            text.Append(segment.Text);
        return text.ToString();
    }

    /// <summary>
    /// Whisper's encoder always works on a 30 s window (1500 frames, 50 per second), even for a 2 s command. Telling it
    /// how much audio there really is (plus a margin) skips most of that work; the floor avoids the garbled text very
    /// small contexts produce.
    /// </summary>
    public static int AudioContextFor(int sampleCount)
    {
        var seconds = sampleCount / (double)SampleRate;
        return Math.Clamp((int)Math.Ceiling(seconds * 50) + 64, 256, 1500);
    }

    // whisper.cpp ignores input under one second, so very short commands get a little silence after them.
    static float[] PadToMinimum(float[] samples)
    {
        var minimum = SampleRate * 11 / 10;
        if (samples.Length >= minimum) return samples;
        var padded = new float[minimum];
        samples.CopyTo(padded, 0);
        return padded;
    }

    // Whisper marks non-speech as [Música], (risos)… and on near-silence tends to invent subtitle credits.
    // A leading "Alexa," is dropped: the text already goes straight to Alexa.
    public static string Clean(string text)
    {
        text = NonSpeech().Replace(text, " ").Trim();
        if (text.Contains("amara.org", StringComparison.OrdinalIgnoreCase) || text.Contains("legendas", StringComparison.OrdinalIgnoreCase))
            return "";
        return WakeWord().Replace(text, "").Trim(' ', '.', '…');
    }

    string Fix(string text) => language == "pt" ? FixPortuguese(text) : text;

    // Said quickly, "apaga a luz" blurs into one word and Whisper (any model size) writes a verb with a pronoun:
    // "apagá-los", "apaga-nos", "apaga-lhe", "apagalos", "apocalus". On its own that is never a command, so it is read
    // as the light. Same for "acende".
    public static string FixPortuguese(string text)
    {
        var match = VerbAndPronoun().Match(text);
        if (!match.Success) return text;
        return match.Groups["off"].Success ? "Apaga a luz" : "Acende a luz";
    }

    [GeneratedRegex(@"^(?:(?<off>ap[aoá]r?[gck])|(?<on>a[sc]c?en?d))[aáeéê]?[\s-]*(?:l[aáoóuú][sz]?|n[oó]s|lhes?|mos|[aã]os)(?:-s[oe])?[\s!.?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex VerbAndPronoun();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex NonSpeech();

    // "Alexa", "Alexia", "Aléxi"… as a whole word (not the start of "Alexandre").
    [GeneratedRegex(@"^\s*al[eé]x(?:i?a|i)\b\s*[,.!?]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex WakeWord();
}

/// <summary>
/// Energy-based speech detection on 50 ms chunks: the first 300 ms calibrate the room noise and speech is anything
/// clearly above it. Remembers where speech started and ended so the silence around a command can be cut off.
/// </summary>
sealed class VoiceActivity
{
    const int CalibrationChunks = 6;
    // Generous: the soft end of a word ("luz") is often under the threshold, and anything under ~5 s costs the same to
    // transcribe (see AudioContextFor), so the cut is only worth it for long waits before speaking.
    const int MarginBefore = VoiceInput.SampleRate * 2 / 5; // 400 ms
    const int MarginAfter = VoiceInput.SampleRate * 9 / 10; // 900 ms

    readonly float[] calibration = new float[CalibrationChunks];
    float noiseFloor;
    float lastRms;
    int firstVoice = -1, lastVoiceEnd = -1;

    public int Chunks { get; private set; }
    public bool Heard => firstVoice >= 0;

    // Quiet microphones barely rise above the room noise, so the bar is 2.5× the noise (at least 0.004).
    public float Threshold => Math.Clamp(noiseFloor * 2.5f, 0.004f, 0.08f);

    /// <summary>Feeds one chunk that starts at sample <paramref name="offset"/>; true when it holds speech.</summary>
    public bool Feed(ReadOnlySpan<float> chunk, int offset)
    {
        double energy = 0;
        foreach (var s in chunk) energy += s * s;
        lastRms = (float)Math.Sqrt(energy / Math.Max(1, chunk.Length));
        if (++Chunks <= CalibrationChunks)
        {
            // The median, not the loudest: a click or a command started right away must not set the bar above speech.
            calibration[Chunks - 1] = lastRms;
            var sorted = calibration[..Chunks];
            Array.Sort(sorted);
            noiseFloor = sorted[(Chunks - 1) / 2];
            return false;
        }
        if (lastRms <= Threshold) return false;
        if (firstVoice < 0) firstVoice = offset;
        lastVoiceEnd = offset + chunk.Length;
        return true;
    }

    public string Describe() => $"rms {lastRms:F4} threshold {Threshold:F4} floor {noiseFloor:F4} heard {Heard}";

    /// <summary>The voiced part of <paramref name="samples"/> plus a margin (all of it if no speech was found).</summary>
    public float[] Trim(float[] samples)
    {
        if (!Heard) return samples;
        var start = Math.Max(0, firstVoice - MarginBefore);
        var end = Math.Min(samples.Length, lastVoiceEnd + MarginAfter);
        return samples[start..end];
    }

    /// <summary>Runs the detector over a whole recording (WAV files in diagnostics and tests).</summary>
    public static float[] TrimToSpeech(float[] samples)
    {
        var vad = new VoiceActivity();
        const int chunk = VoiceInput.SampleRate / 20;
        for (var offset = 0; offset < samples.Length; offset += chunk)
            vad.Feed(samples.AsSpan(offset, Math.Min(chunk, samples.Length - offset)), offset);
        return vad.Trim(samples);
    }
}
