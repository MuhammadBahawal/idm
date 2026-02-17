using System.Diagnostics;

namespace MyDM.Core.Media;

/// <summary>
/// Wraps ffmpeg CLI for muxing audio+video streams and media conversion.
/// </summary>
public class FfmpegMuxer
{
    private string _ffmpegPath;

    public FfmpegMuxer(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? FindFfmpeg();
    }

    public string FfmpegPath
    {
        get => _ffmpegPath;
        set => _ffmpegPath = value;
    }

    public bool IsAvailable => !string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath);

    /// <summary>
    /// Mux separate video and audio files into a single MP4.
    /// </summary>
    public async Task<bool> MuxAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("ffmpeg not found. Please install ffmpeg or configure the path in Settings.");

        var args = $"-i \"{videoPath}\" -i \"{audioPath}\" -c copy -movflags +faststart \"{outputPath}\" -y";
        return await RunFfmpegAsync(args, ct);
    }

    /// <summary>
    /// Concatenate multiple segment files into a single output.
    /// </summary>
    public async Task<bool> ConcatSegmentsAsync(IEnumerable<string> segmentFiles, string outputPath, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("ffmpeg not found.");

        // Create a concat list file
        var listFile = Path.GetTempFileName();
        try
        {
            var lines = segmentFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(listFile, lines, ct);

            var args = $"-f concat -safe 0 -i \"{listFile}\" -c copy -movflags +faststart \"{outputPath}\" -y";
            return await RunFfmpegAsync(args, ct);
        }
        finally
        {
            try { File.Delete(listFile); } catch { }
        }
    }

    /// <summary>
    /// Convert TS segments to MP4.
    /// </summary>
    public async Task<bool> ConvertToMp4Async(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("ffmpeg not found.");

        var args = $"-i \"{inputPath}\" -c copy -movflags +faststart \"{outputPath}\" -y";
        return await RunFfmpegAsync(args, ct);
    }

    /// <summary>
    /// Get ffmpeg version string.
    /// </summary>
    public async Task<string?> GetVersionAsync()
    {
        if (!IsAvailable) return null;

        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath, "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = await process.StandardOutput.ReadLineAsync();
            await process.WaitForExitAsync();
            return output;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> RunFfmpegAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffmpegPath, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };

        using var process = Process.Start(psi);
        if (process == null) return false;

        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new Exception($"ffmpeg exited with code {process.ExitCode}: {stderr[..Math.Min(stderr.Length, 500)]}");
        }

        return true;
    }

    private static string FindFfmpeg()
    {
        // Check common locations on Windows
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        };

        foreach (var path in paths)
        {
            if (File.Exists(path)) return path;
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(fullPath)) return fullPath;
        }

        return "ffmpeg"; // Hope it's on PATH
    }
}
