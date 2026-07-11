using System;
using System.IO;
using xPasteWin.Models;
using Xunit;

public class ClipboardItemTests
{
    [Fact]
    public void MakeHash_encodes_count_prefix_suffix()
    {
        var data = new byte[40];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
        var h = ClipboardItem.MakeHash(data);
        Assert.StartsWith("40-", h);
        Assert.Contains("000102030405060708090a0b0c0d0e0f", h); // prefix 16
        Assert.EndsWith("18191a1b1c1d1e1f2021222324252627", h); // suffix 16
    }

    [Fact]
    public void DisplayText_for_file_joins_filenames()
    {
        var item = new ClipboardItem
        {
            Type = ClipboardContentType.File,
            FilePaths = new[] { @"C:\a\one.txt", @"C:\b\two.txt" }
        };
        Assert.Equal("one.txt, two.txt", item.DisplayText);
    }

    [Fact]
    public void AllDirectories_true_only_when_every_path_is_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        Assert.True(ClipboardItem.AllDirectories(new[] { dir }));
        var file = Path.Combine(dir, "f.txt");
        File.WriteAllText(file, "x");
        Assert.False(ClipboardItem.AllDirectories(new[] { file }));
    }
}
