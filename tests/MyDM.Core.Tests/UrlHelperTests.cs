using MyDM.Core.Utilities;
using Xunit;

namespace MyDM.Core.Tests;

public class UrlHelperTests
{
    [Theory]
    [InlineData("https://example.com/file.zip", true)]
    [InlineData("http://example.com/file.exe", true)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    [InlineData("ftp://files.example.com/doc.pdf", false)] // Only HTTP/HTTPS supported
    public void IsValidUrl_ShouldValidateCorrectly(string url, bool expected)
    {
        Assert.Equal(expected, UrlHelper.IsValidUrl(url));
    }

    [Theory]
    [InlineData("https://example.com/file.zip", "file.zip")]
    [InlineData("https://example.com/path/to/document.pdf", "document.pdf")]
    [InlineData("https://example.com/path/to/file.exe?param=val", "file.exe")]
    public void ExtractFileName_ShouldExtractCorrectly(string url, string expected)
    {
        Assert.Equal(expected, UrlHelper.ExtractFileName(url));
    }

    [Fact]
    public void ExtractFileName_RootUrl_ShouldGenerateName()
    {
        var result = UrlHelper.ExtractFileName("https://example.com/");
        Assert.StartsWith("download_", result);
    }

    [Theory]
    [InlineData("file<>name.txt")]
    [InlineData("normal.txt")]
    [InlineData("path/traversal/../bad.txt")]
    public void SanitizeFileName_ShouldRemoveUnsafeChars(string input)
    {
        var result = UrlHelper.SanitizeFileName(input);
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
    }
}
