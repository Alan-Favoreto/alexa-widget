using System.Text.Json;

namespace AlexaWidget.Tests;

public sealed class StorageTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("alexawidget-tests-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void SafeFileReplacesWholeAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(dir, "a.json");
        SafeFile.WriteAllText(path, "primeiro conteúdo, mais longo");
        SafeFile.WriteAllText(path, "segundo");

        Assert.Equal("segundo", File.ReadAllText(path));
        Assert.Equal(["a.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void SafeFileWaitsForAFileBrieflyLockedByAnotherProgram()
    {
        var path = Path.Combine(dir, "locked.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Delay(250).ContinueWith(_ => holder.Dispose());

        Assert.Equal([1, 2, 3], SafeFile.ReadAllBytes(path));
    }

    [Fact]
    public void LocalDataRoundTripsAndRejectsPathTricks()
    {
        using var doc = JsonDocument.Parse("""[{"text":"Que horas são?"}]""");
        LocalData.Save(dir, "quick-commands", doc.RootElement);

        Assert.Equal("Que horas são?", LocalData.Load(dir, "quick-commands")!.Value[0].GetProperty("text").GetString());
        Assert.Null(LocalData.Load(dir, "missing"));
        Assert.Throws<ArgumentException>(() => LocalData.Load(dir, "../auth"));
        Assert.Throws<ArgumentException>(() => LocalData.Load(dir, "Upper"));
    }

    [Fact]
    public void RegionDefaultsToBrazilAndCreatesTheFile()
    {
        var region = Region.Load(dir);

        Assert.Equal(new Region("amazon.com.br", "pt-BR"), region);
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void RegionKeepsAHandEditWithATypo()
    {
        var path = Path.Combine(dir, "settings.json");
        const string typo = """{ "AmazonDomain": "amazon.com", "Locale": "en-US" """;
        File.WriteAllText(path, typo);

        Assert.Equal("amazon.com.br", Region.Load(dir).AmazonDomain);
        Assert.Equal(typo, File.ReadAllText(path)); // not overwritten: the user can fix the typo
    }

    [Theory]
    [InlineData("amazon.com.br", "_br", "alexa.amazon.com.br")]
    [InlineData("amazon.com", "", "alexa.amazon.com")]
    [InlineData("amazon.co.uk", "_uk", "alexa.amazon.co.uk")]
    public void RegionHosts(string domain, string pageHandle, string alexaHost)
    {
        var region = new Region(domain, "pt-BR");
        Assert.Equal(pageHandle, region.PageHandle);
        Assert.Equal(alexaHost, region.AlexaHost);
    }

    [Fact]
    public void PrefsSurviveAndFallBackToDefaults()
    {
        Assert.Equal("Ctrl+Alt+A", Prefs.Load(dir).HotkeyLabel);

        var prefs = new Prefs { MouseButton = "XButton1", HotkeyLabel = null };
        prefs.Save(dir);
        var loaded = Prefs.Load(dir);
        Assert.Equal("XButton1", loaded.MouseButton);
        Assert.Null(loaded.HotkeyLabel);

        File.WriteAllText(Path.Combine(dir, "prefs.json"), "{ quebrado");
        Assert.Equal("Ctrl+Alt+A", Prefs.Load(dir).HotkeyLabel);
    }
}
