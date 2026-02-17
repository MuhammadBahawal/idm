using MyDM.Core.Parsers;
using Xunit;

namespace MyDM.Core.Tests;

public class MimeDetectorTests
{
    [Theory]
    [InlineData("video/mp4", "Video")]
    [InlineData("video/webm", "Video")]
    [InlineData("audio/mpeg", "Music")]
    [InlineData("application/pdf", "Documents")]
    [InlineData("application/zip", "Compressed")]
    [InlineData("application/x-msdownload", "Programs")]
    [InlineData("image/png", "Others")]
    [InlineData("text/html", "Others")]
    public void DetectFromMime_ShouldClassifyCorrectly(string mime, string expectedCategory)
    {
        var result = MimeDetector.DetectFromMime(mime);
        Assert.Equal(expectedCategory, result);
    }

    [Theory]
    [InlineData(".mp4", "Video")]
    [InlineData(".mkv", "Video")]
    [InlineData(".mp3", "Music")]
    [InlineData(".pdf", "Documents")]
    [InlineData(".zip", "Compressed")]
    [InlineData(".7z", "Compressed")]
    [InlineData(".exe", "Programs")]
    [InlineData(".msi", "Programs")]
    [InlineData(".unknown", "Others")]
    public void DetectFromExtension_ShouldClassifyCorrectly(string extension, string expectedCategory)
    {
        var result = MimeDetector.DetectFromExtension(extension);
        Assert.Equal(expectedCategory, result);
    }

    [Fact]
    public void Detect_PrefersExtension_OverMime()
    {
        // URL with .mp4 should classify as Video even if MIME is text/plain
        var result = MimeDetector.Detect("https://example.com/video.mp4", "text/plain");
        Assert.Equal("Video", result);
    }

    [Fact]
    public void IsDownloadable_ShouldDetectKnownTypes()
    {
        Assert.True(MimeDetector.IsDownloadable("https://example.com/file.zip", null));
        Assert.True(MimeDetector.IsDownloadable(null, "application/octet-stream"));
        Assert.False(MimeDetector.IsDownloadable(null, "text/html"));
    }
}
