using MyDM.Core.Parsers;
using Xunit;

namespace MyDM.Core.Tests;

public class DashParserTests
{
    [Fact]
    public void ParseMpd_ShouldExtractRepresentations()
    {
        var mpd = @"<?xml version=""1.0""?>
<MPD xmlns=""urn:mpeg:dash:schema:mpd:2011"" type=""static"" mediaPresentationDuration=""PT0H3M30.5S"">
  <Period>
    <AdaptationSet mimeType=""video/mp4"" contentType=""video"">
      <Representation id=""1"" bandwidth=""500000"" width=""640"" height=""360"">
        <BaseURL>video_360p.mp4</BaseURL>
      </Representation>
      <Representation id=""2"" bandwidth=""1500000"" width=""1280"" height=""720"">
        <BaseURL>video_720p.mp4</BaseURL>
      </Representation>
      <Representation id=""3"" bandwidth=""4000000"" width=""1920"" height=""1080"">
        <BaseURL>video_1080p.mp4</BaseURL>
      </Representation>
    </AdaptationSet>
    <AdaptationSet mimeType=""audio/mp4"" contentType=""audio"">
      <Representation id=""4"" bandwidth=""128000"">
        <BaseURL>audio_128k.mp4</BaseURL>
      </Representation>
    </AdaptationSet>
  </Period>
</MPD>";

        var result = DashParser.ParseMpd(mpd, "https://cdn.example.com/dash/");

        Assert.NotNull(result);
        // Should only get video representations (3 items)
        Assert.True(result.Count >= 3);

        // Should be sorted by bandwidth (highest first)
        Assert.Equal("1920x1080", result[0].Resolution);
        Assert.Equal(4000000, result[0].Bandwidth);
    }

    [Fact]
    public void ParseMpd_EmptyContent_ShouldReturnEmpty()
    {
        var result = DashParser.ParseMpd("", "https://example.com/");
        Assert.Empty(result);
    }

    [Fact]
    public void IsMpd_ShouldDetectCorrectly()
    {
        Assert.True(DashParser.IsMpd("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\">"));
        Assert.False(DashParser.IsMpd("not a dash manifest"));
    }
}
