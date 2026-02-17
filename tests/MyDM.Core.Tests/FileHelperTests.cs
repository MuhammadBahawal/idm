using MyDM.Core.Utilities;
using Xunit;

namespace MyDM.Core.Tests;

public class FileHelperTests
{
    [Theory]
    [InlineData(0, "0.00 B")]
    [InlineData(512, "512.00 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(1073741824, "1.00 GB")]
    public void FormatSize_ShouldFormatCorrectly(long bytes, string expected)
    {
        Assert.Equal(expected, FileHelper.FormatSize(bytes));
    }

    [Fact]
    public void FormatSpeed_ShouldFormatCorrectly()
    {
        Assert.Equal("0.00 B/s", FileHelper.FormatSpeed(0));
        Assert.Contains("/s", FileHelper.FormatSpeed(1024));
        Assert.Contains("KB", FileHelper.FormatSpeed(1024));
        Assert.Contains("MB", FileHelper.FormatSpeed(1048576));
    }

    [Fact]
    public void FormatTimeLeft_ShouldFormatCorrectly()
    {
        var five = FileHelper.FormatTimeLeft(TimeSpan.FromSeconds(5));
        Assert.NotNull(five);
        Assert.Contains("5", five!);

        var threeMins = FileHelper.FormatTimeLeft(TimeSpan.FromMinutes(3));
        Assert.NotNull(threeMins);

        var twoHours = FileHelper.FormatTimeLeft(TimeSpan.FromHours(2));
        Assert.NotNull(twoHours);
    }
}
