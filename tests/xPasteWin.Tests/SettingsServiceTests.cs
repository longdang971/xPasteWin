using System;
using System.IO;
using xPasteWin.Services;
using Xunit;

public class SettingsServiceTests
{
    [Fact]
    public void Get_returns_fallback_when_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var s = new SettingsService(dir);
        Assert.Equal("bottom", s.Get("panelPosition", "bottom"));
        Assert.Equal(4, s.Get("keepHistoryIndex", 4));
    }

    [Fact]
    public void Set_then_Get_persists_across_instances()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        new SettingsService(dir).Set("panelPosition", "left");
        Assert.Equal("left", new SettingsService(dir).Get("panelPosition", "bottom"));
    }
}
