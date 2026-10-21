using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;
using Whisper.net;

namespace AlexaWidget;

/// <summary>
/// Records one utterance from the default microphone (stops on silence) and transcribes it offline with Whisper.
/// The model is downloaded once to %LOCALAPPDATA%\AlexaWidget\models.
/// </summary>
sealed partial class VoiceInput(string dataDir, string language)
{
    const string ModelName = "ggml-small-q5_1.bin";
    const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelName;
    const int SampleRate = 16000;
    static readonly TimeSpan MaxUtterance = TimeSpan.FromSeconds(12);
    static readonly TimeSpan WaitForSpeech = TimeSpan.FromSeconds(6);
    static readonly TimeSpan TrailingSilence = TimeSpan.FromMilliseconds(1300);

    readonly string modelPath = Path.Combine(dataDir, "models", ModelName);
    readonly SemaphoreSlim busy = new(1, 1);
    readonly Lock factoryLock = new();
    Task<WhisperFactory>? factory;
    CancellationTokenSource? session;

    /// <summary>Diagnostics hook: receives a line per audio chunk (level vs. threshold).</summary>
    public Action<string>? Trace { get; set; }

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
            var samples = await RecordAsync(stop.Token, () =>
            {
                Trace?.Invoke("listen: capturing");
                status("listening", null);
            });
            Trace?.Invoke($"listen: recorded {samples.Length / (double)SampleRate:F1} s (stopped by user: {stop.IsCancellationRequested})");
            if (samples.Length < SampleRate / 3) return "";
            status("transcribing", null);
            var factoryReady = await model;
            Trace?.Invoke("listen: model ready");
            var text = Clean(await Task.Run(() => TranscribeAsync(factoryReady, samples)));
            Trace?.Invoke($"listen: transcribed \"{text}\"");
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

    /// <summary>Diagnostics: transcribes a 16 kHz mono 16-bit WAV file.</summary>
    public async Task<string> TranscribeFileAsync(string wavPath, Action<string, int?> status)
    {
        await EnsureModelAsync(status);
        using var reader = new WaveFileReader(wavPath);
        var provider = reader.ToSampleProvider();
        var samples = new float[reader.SampleCount];
        var read = provider.Read(samples, 0, samples.Length);
        return Clean(await TranscribeAsync(await LoadFactory(), samples[..read]));
    }

    async Task EnsureModelAsync(Action<string, int?> status)
    {
        if (File.Exists(modelPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        var partial = modelPath + ".part";
        status("downloading", 0);

        using var http = new HttpClient();
        using var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
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
                read += n;
                var percent = total > 0 ? (int)(read * 100 / total) : 0;
                if (percent != lastPercent) status("downloading", lastPercent = percent);
            }
        }
        File.Move(partial, modelPath, overwrite: true);
    }

    async Task<float[]> RecordAsync(CancellationToken userStop, Action onStarted)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(userStop);
        stop.CancelAfter(MaxUtterance);
        var samples = new List<float>(SampleRate * (int)MaxUtterance.TotalSeconds);
        var stopped = new TaskCompletionSource();
        var started = DateTime.UtcNow;
        var lastVoice = started;
        var heard = false;
        var chunks = 0;
        var noiseFloor = 0f;

        using var input = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
        input.DataAvailable += (_, e) =>
        {
            var count = e.BytesRecorded / 2;
            double energy = 0;
            for (var i = 0; i < count; i++)
            {
                var s = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                samples.Add(s);
                energy += s * s;
            }
            var rms = (float)Math.Sqrt(energy / Math.Max(1, count));

            // The first 300 ms calibrate the room noise; speech is anything clearly above it.
            if (++chunks <= 6)
            {
                noiseFloor = Math.Max(noiseFloor, rms);
                return;
            }
            var threshold = Math.Clamp(noiseFloor * 3f, 0.008f, 0.08f);
            var now = DateTime.UtcNow;
            if (chunks % 10 == 0) Trace?.Invoke($"rms {rms:F4} threshold {threshold:F4} floor {noiseFloor:F4} heard {heard}");
            if (rms > threshold)
            {
                heard = true;
                lastVoice = now;
            }
            if (heard ? now - lastVoice > TrailingSilence : now - started > WaitForSpeech)
                stop.Cancel(); // safe from the capture thread: the registration below only calls StopRecording
        };
        input.RecordingStopped += (_, e) =>
        {
            if (e.Exception is { } ex) stopped.TrySetException(ex);
            else stopped.TrySetResult();
        };

        if (stop.IsCancellationRequested) return [];
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
        return heard ? [.. samples] : [];
    }

    async Task<string> TranscribeAsync(WhisperFactory whisper, float[] samples)
    {
        await using var processor = whisper.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Max(2, Environment.ProcessorCount / 2))
            .Build();
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples))
            text.Append(segment.Text);
        return text.ToString();
    }

    // Whisper marks non-speech as [Música], (risos)… and on near-silence tends to invent subtitle credits.
    // A leading "Alexa," is dropped: the text already goes straight to Alexa.
    static string Clean(string text)
    {
        text = NonSpeech().Replace(text, " ").Trim();
        if (text.Contains("amara.org", StringComparison.OrdinalIgnoreCase) || text.Contains("legendas", StringComparison.OrdinalIgnoreCase))
            return "";
        return WakeWord().Replace(text, "").Trim(' ', '.', '…');
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex NonSpeech();

    [GeneratedRegex(@"^\s*al[eé]x[ai]\s*[,.!?]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex WakeWord();
}
