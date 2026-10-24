namespace AlexaWidget.Tests;

public class ShortcutTests
{
    const int Mute = 0xAD, CtrlAlt = Prefs.ModControl | Prefs.ModAlt;

    [Fact]
    public void PlainCombinationsUseRegisterHotKey()
    {
        var (plain, viaHook, failed) = Shortcuts.Split([new(1, CtrlAlt, 'A'), new(100, CtrlAlt, 'B')]);

        Assert.Equal([1, 100], plain.Select(s => s.Id));
        Assert.Empty(viaHook);
        Assert.Empty(failed);
    }

    [Fact]
    public void AMultimediaKeyAloneGoesThroughTheHook()
    {
        var (plain, viaHook, _) = Shortcuts.Split([new(1, 0, Mute)]);

        Assert.Empty(plain);
        Assert.Equal(1, Assert.Single(viaHook).Id);
    }

    [Fact]
    public void ADoublePressTakesTheSinglePressOfTheSameKeyAlong()
    {
        // The hook has to count every press of Ctrl+Alt+A, so RegisterHotKey must not grab the first one.
        var (plain, viaHook, _) = Shortcuts.Split([new(1, CtrlAlt, 'A'), new(100, CtrlAlt, 'A', Taps: 2), new(101, CtrlAlt, 'B')]);

        Assert.Equal([101], plain.Select(s => s.Id));
        Assert.Equal([1, 100], viaHook.Select(s => s.Id));
    }

    [Fact]
    public void TheSameKeyAndPressCountTwiceFails()
    {
        var (_, viaHook, failed) = Shortcuts.Split([new(100, 0, Mute, Taps: 2), new(101, 0, Mute, Taps: 2), new(102, 0, Mute)]);

        Assert.Equal([100, 102], viaHook.Select(s => s.Id));
        Assert.Equal([101], failed);
    }

    [Theory]
    [InlineData(0xAD, true)]  // mute (the knob click)
    [InlineData(0xAF, true)]  // volume up (the knob turning)
    [InlineData(0xB3, true)]  // play/pause
    [InlineData('A', false)]
    [InlineData(0x70, false)] // F1
    public void MultimediaKeys(int key, bool media) => Assert.Equal(media, KeyTapHook.IsMediaKey(key));
}
