namespace AlexaWidget.Tests;

public class VoiceActivityTests
{
    const int Rate = VoiceInput.SampleRate;

    /// <summary>Steady low noise with a loud 440 Hz "voice" between two times (in seconds).</summary>
    static float[] Clip(double seconds, double voiceFrom, double voiceTo, float noise = 0.002f, float voice = 0.1f)
    {
        var random = new Random(1);
        var samples = new float[(int)(seconds * Rate)];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)Rate;
            samples[i] = (float)((random.NextDouble() * 2 - 1) * noise);
            if (t >= voiceFrom && t < voiceTo) samples[i] += (float)(Math.Sin(2 * Math.PI * 440 * t) * voice);
        }
        return samples;
    }

    [Fact]
    public void KeepsOnlyTheSpeechWithAMargin()
    {
        var trimmed = VoiceActivity.TrimToSpeech(Clip(seconds: 5, voiceFrom: 2, voiceTo: 3));

        // 1 s of speech + 400 ms before + 900 ms after (to the 50 ms chunk the detection works in).
        Assert.InRange(trimmed.Length / (double)Rate, 2.25, 2.4);
    }

    [Fact]
    public void QuietMicrophoneStillCounts()
    {
        // Speech only ~3× the room noise, like the levels seen in a real voice.log.
        var trimmed = VoiceActivity.TrimToSpeech(Clip(seconds: 4, voiceFrom: 1.5, voiceTo: 2.5, noise: 0.003f, voice: 0.012f));

        Assert.InRange(trimmed.Length / (double)Rate, 2.25, 2.4);
    }

    [Fact]
    public void NoSpeechKeepsEverything()
    {
        var silence = Clip(seconds: 3, voiceFrom: 0, voiceTo: 0);

        Assert.Equal(silence.Length, VoiceActivity.TrimToSpeech(silence).Length);
    }

    [Fact]
    public void ALoudClickWhileCalibratingDoesNotDeafen()
    {
        var samples = Clip(seconds: 4, voiceFrom: 1.5, voiceTo: 2.5, noise: 0.003f, voice: 0.012f);
        for (var i = Rate / 10; i < Rate / 10 + Rate / 20; i++) samples[i] = 0.3f; // a click in the 3rd chunk

        Assert.InRange(VoiceActivity.TrimToSpeech(samples).Length / (double)Rate, 2.25, 2.4);
    }

    [Fact]
    public void TheFirst300MillisecondsOnlyCalibrate()
    {
        var vad = new VoiceActivity();
        var loud = Enumerable.Repeat(0.05f, Rate / 20).ToArray();

        for (var i = 0; i < 6; i++) Assert.False(vad.Feed(loud, i * loud.Length));
        Assert.False(vad.Heard);
    }

    [Theory]
    [InlineData(1, 256)]    // 50 frames + 64 margin, raised to the floor
    [InlineData(4, 264)]    // 200 + 64
    [InlineData(10, 564)]   // 500 + 64
    [InlineData(40, 1500)]  // never more than Whisper's full 30 s window
    public void AudioContextCoversTheClip(int seconds, int expected) =>
        Assert.Equal(expected, VoiceInput.AudioContextFor(seconds * Rate));
}

public class TranscriptCleanupTests
{
    [Theory]
    [InlineData(" Alexa, liga a luz.", "liga a luz")]
    [InlineData("Alexia que horas são?", "que horas são?")]
    [InlineData("Aléxa! Toca música", "Toca música")]
    [InlineData("Alexandre chegou", "Alexandre chegou")]
    [InlineData("[Música] toca rock", "toca rock")]
    [InlineData("(risos) apaga a luz…", "apaga a luz")]
    [InlineData("Legendas pela comunidade Amara.org", "")]
    [InlineData("*som de chuva*", "")]
    public void RemovesNoiseMarksAndTheWakeWord(string raw, string expected) =>
        Assert.Equal(expected, VoiceInput.Clean(raw));
}

public class QuickCommandTests
{
    [Theory]
    // What Whisper wrote for a quick "apaga a luz" in real recordings.
    [InlineData("Apagá-los")]
    [InlineData("Apaga-los!")]
    [InlineData("Aparga-los")]
    [InlineData("Apaga-lo-so")]
    [InlineData("Apaga-nos")]
    [InlineData("Apaga-lhe")]
    [InlineData("Apagalos")]
    [InlineData("Apagaluz")]
    [InlineData("Apacalus")]
    [InlineData("Apocalus")]
    [InlineData("apagamos")]
    [InlineData("Apagãos")]
    public void AVerbWithAPronounIsTheLight(string heard) =>
        Assert.Equal("Apaga a luz", VoiceInput.FixPortuguese(heard));

    [Theory]
    [InlineData("Acende-los")]
    [InlineData("Acendê-los")]
    [InlineData("Acendemos")]
    public void TurningOnToo(string heard) =>
        Assert.Equal("Acende a luz", VoiceInput.FixPortuguese(heard));

    [Theory]
    [InlineData("Apaga a luz")]
    [InlineData("Apaga a luz da escada")]
    [InlineData("apaga as luzes")]
    [InlineData("Acende a luz do quarto")]
    [InlineData("Apocalipse")]
    [InlineData("Para")]
    [InlineData("Toca música")]
    public void RealCommandsAreKept(string heard) =>
        Assert.Equal(heard, VoiceInput.FixPortuguese(heard));
}
