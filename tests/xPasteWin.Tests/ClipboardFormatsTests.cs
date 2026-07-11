using xPasteWin.Interop;
using Xunit;

public class ClipboardFormatsTests
{
    [Fact]
    public void BuildCfHtml_has_valid_offsets_and_roundtrips()
    {
        var fragment = "<b>hi</b>";
        var cf = ClipboardFormats.BuildCfHtml(fragment);
        Assert.Contains("Version:0.9", cf);
        Assert.Contains("<!--StartFragment-->", cf);
        Assert.Contains("<!--EndFragment-->", cf);
        Assert.Equal(fragment, ClipboardFormats.ExtractCfHtmlFragment(cf));
    }

    [Fact]
    public void ExtractCfHtmlFragment_returns_null_without_markers()
    {
        Assert.Null(ClipboardFormats.ExtractCfHtmlFragment("<p>no markers</p>"));
    }
}
