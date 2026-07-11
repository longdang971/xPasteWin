using System;
using System.Text;

namespace xPasteWin.Interop;

public static class ClipboardFormats
{
    private const string StartFrag = "<!--StartFragment-->";
    private const string EndFrag = "<!--EndFragment-->";

    // CF_HTML yêu cầu header với offset byte (UTF-8) tới các mốc.
    public static string BuildCfHtml(string htmlFragment)
    {
        string Body(int startHtml, int endHtml, int startFrag, int endFrag) =>
            "Version:0.9\r\n" +
            $"StartHTML:{startHtml:D10}\r\n" +
            $"EndHTML:{endHtml:D10}\r\n" +
            $"StartFragment:{startFrag:D10}\r\n" +
            $"EndFragment:{endFrag:D10}\r\n" +
            "<html><body>" + StartFrag + htmlFragment + EndFrag + "</body></html>";

        var placeholder = Body(0, 0, 0, 0);
        var payload = "<html><body>" + StartFrag + htmlFragment + EndFrag + "</body></html>";
        int headerLen = Encoding.UTF8.GetByteCount(placeholder) - Encoding.UTF8.GetByteCount(payload);

        int startHtmlOffset = headerLen;
        int startFragment = startHtmlOffset + Encoding.UTF8.GetByteCount("<html><body>" + StartFrag);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(htmlFragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(EndFrag + "</body></html>");

        return Body(startHtmlOffset, endHtml, startFragment, endFragment);
    }

    public static string? ExtractCfHtmlFragment(string cfHtml)
    {
        int s = cfHtml.IndexOf(StartFrag, StringComparison.Ordinal);
        int e = cfHtml.IndexOf(EndFrag, StringComparison.Ordinal);
        if (s < 0 || e < 0 || e < s) return null;
        s += StartFrag.Length;
        return cfHtml.Substring(s, e - s);
    }
}
