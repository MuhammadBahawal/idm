using MyDM.Core.Parsers;
using Xunit;

namespace MyDM.Core.Tests;

public class HlsParserTests
{
    [Fact]
    public void ParseMasterPlaylist_ShouldExtractVariants()
    {
        var masterPlaylist = @"#EXTM3U
#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=""avc1.4d001e,mp4a.40.2""
360p.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=1400000,RESOLUTION=854x480,CODECS=""avc1.4d001f,mp4a.40.2""
480p.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1280x720,CODECS=""avc1.4d001f,mp4a.40.2""
720p.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS=""avc1.640028,mp4a.40.2""
1080p.m3u8";

        var result = HlsParser.ParseMasterPlaylist(masterPlaylist, "https://cdn.example.com/stream/");

        Assert.Equal(4, result.Count);

        // Should be sorted by bandwidth (highest first)
        Assert.Equal("1920x1080", result[0].Resolution);
        Assert.Equal(5000000, result[0].Bandwidth);
        Assert.Equal("https://cdn.example.com/stream/1080p.m3u8", result[0].Url);

        Assert.Equal("640x360", result[3].Resolution);
    }

    [Fact]
    public void ParseMasterPlaylist_EmptyContent_ShouldReturnEmpty()
    {
        var result = HlsParser.ParseMasterPlaylist("", "https://example.com/");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseMediaPlaylist_ShouldExtractSegments()
    {
        var mediaPlaylist = @"#EXTM3U
#EXT-X-TARGETDURATION:10
#EXT-X-MEDIA-SEQUENCE:0
#EXTINF:9.009,
seg-0.ts
#EXTINF:9.009,
seg-1.ts
#EXTINF:9.009,
seg-2.ts
#EXT-X-ENDLIST";

        var segments = HlsParser.ParseMediaPlaylist(mediaPlaylist, "https://cdn.example.com/stream/");

        Assert.Equal(3, segments.Count);
        Assert.Equal("https://cdn.example.com/stream/seg-0.ts", segments[0]);
    }

    [Fact]
    public void IsMasterPlaylist_ShouldDetectCorrectly()
    {
        Assert.True(HlsParser.IsMasterPlaylist("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000"));
        Assert.False(HlsParser.IsMasterPlaylist("#EXTM3U\n#EXTINF:10,\nseg0.ts"));
    }

    [Fact]
    public void IsHlsPlaylist_ShouldDetectCorrectly()
    {
        Assert.True(HlsParser.IsHlsPlaylist("#EXTM3U\ntest"));
        Assert.False(HlsParser.IsHlsPlaylist("not an hls playlist"));
    }
}
