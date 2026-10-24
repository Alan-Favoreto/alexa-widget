namespace AlexaWidget;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args is ["--dump-notifications", var outPath])
        {
            Diagnostics.DumpNotificationsAsync(outPath).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--transcribe", var wavPath, var resultPath])
        {
            var dataDir0 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
            var started = DateTime.Now;
            var text = new VoiceInput(dataDir0, "pt").TranscribeFileAsync(wavPath, (_, _) => { }).GetAwaiter().GetResult();
            File.WriteAllText(resultPath, $"{text}\n({(DateTime.Now - started).TotalSeconds:F1} s incl. download)");
            return;
        }
        if (args is ["--voice-bench", var benchOut, var benchLanguage, .. var benchWavs] && benchWavs.Length > 0)
        {
            Diagnostics.VoiceBenchAsync(benchOut, benchLanguage, benchWavs).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--record-samples", var samplesDir, var samplesCount])
        {
            Diagnostics.RecordSamplesAsync(samplesDir, int.Parse(samplesCount)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--listen-test", var listenLog, ..] && args.Length <= 3)
        {
            ApplicationConfiguration.Initialize();
            Diagnostics.ListenTest(listenLog, args.Length == 3 ? int.Parse(args[2]) : null);
            return;
        }
        if (args is ["--login-selftest", var loginOut])
        {
            Diagnostics.LoginSelfTest(loginOut);
            return;
        }
        if (args is ["--dump-features", var featuresOut])
        {
            Diagnostics.DumpFeaturesAsync(featuresOut).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--probe-send", var sendOut, .. var triples])
        {
            Diagnostics.ProbeSendAsync(sendOut, triples).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--probe-get", var getOut, .. var getPaths])
        {
            Diagnostics.ProbeGetAsync(getOut, getPaths).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--notification-selftest", var selfTestPath])
        {
            Diagnostics.NotificationSelfTestAsync(selfTestPath).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--dump-routines", var routinesPath])
        {
            Diagnostics.DumpRoutinesAsync(routinesPath).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--probe-notifications", var probePath])
        {
            Diagnostics.ProbeNotificationsAsync(probePath).GetAwaiter().GetResult();
            return;
        }

        using var mutex = new Mutex(true, "AlexaWidget.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            // Already running (maybe hidden in the tray): ask it to come forward instead.
            SingleInstance.SignalShow(args.Contains("--app"));
            return;
        }

        ApplicationConfiguration.Initialize();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        Directory.CreateDirectory(dataDir);
        MainForm form;
        try
        {
            form = new MainForm(dataDir, Region.Load(dataDir), startInAppMode: args.Contains("--app"));
        }
        catch (IOException e)
        {
            MessageBox.Show(e.Message, "Alexa Widget", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(form);
    }
}
