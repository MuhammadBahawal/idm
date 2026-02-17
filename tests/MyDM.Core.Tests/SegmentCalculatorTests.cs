using Xunit;

namespace MyDM.Core.Tests;

/// <summary>
/// Tests for segment calculation logic.
/// Uses an inline SegmentCalculator since the actual engine
/// handles segments internally.
/// </summary>
public class SegmentCalculatorTests
{
    [Fact]
    public void CalculateSegments_ShouldDivideFile()
    {
        long fileSize = 100_000;
        int connections = 4;

        var segments = Calculate(fileSize, connections);

        Assert.True(segments.Count > 0);
        // Segments should cover the entire file
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(fileSize - 1, segments[^1].End);

        // No gaps between segments
        for (int i = 1; i < segments.Count; i++)
        {
            Assert.Equal(segments[i - 1].End + 1, segments[i].Start);
        }
    }

    [Fact]
    public void CalculateSegments_SmallFile_ShouldUseSingleSegment()
    {
        long fileSize = 100;
        int connections = 8;

        var segments = Calculate(fileSize, connections);

        Assert.Single(segments);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(99, segments[0].End);
    }

    [Fact]
    public void CalculateSegments_ZeroSize_ShouldReturnSingleSegment()
    {
        var segments = Calculate(0, 4);
        Assert.Single(segments);
    }

    // ──── Inline helper (mirrors engine logic) ────

    private record Segment(long Start, long End);

    private static List<Segment> Calculate(long totalSize, int connections)
    {
        if (totalSize <= 0) return new List<Segment> { new(0, 0) };

        const long minSegmentSize = 65_536; // 64 KB minimum
        var effectiveConnections = Math.Max(1, Math.Min(connections, (int)(totalSize / minSegmentSize)));
        if (effectiveConnections == 0) effectiveConnections = 1;

        var segmentSize = totalSize / effectiveConnections;
        var segments = new List<Segment>();

        for (int i = 0; i < effectiveConnections; i++)
        {
            long start = i * segmentSize;
            long end = (i == effectiveConnections - 1) ? totalSize - 1 : (start + segmentSize - 1);
            segments.Add(new(start, end));
        }

        return segments;
    }
}
