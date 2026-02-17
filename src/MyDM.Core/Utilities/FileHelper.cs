using System.Security.Cryptography;

namespace MyDM.Core.Utilities;

public static class FileHelper
{
    /// <summary>
    /// Atomically renames a .part file to the final filename.
    /// If the destination already exists, appends a number.
    /// </summary>
    public static string AtomicRename(string partFilePath, string finalFilePath)
    {
        var actualPath = GetUniqueFilePath(finalFilePath);
        File.Move(partFilePath, actualPath, overwrite: false);
        return actualPath;
    }

    /// <summary>
    /// Generates a unique file path by appending (1), (2), etc. if the file already exists.
    /// </summary>
    public static string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath))
            return filePath;

        var dir = Path.GetDirectoryName(filePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var counter = 1;

        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        } while (File.Exists(newPath));

        return newPath;
    }

    /// <summary>
    /// Computes SHA-256 hash of a file.
    /// </summary>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Merges segment temp files into the final .part file in order.
    /// </summary>
    public static async Task MergeSegmentFilesAsync(IEnumerable<string> segmentFiles, string outputFile, CancellationToken ct = default)
    {
        using var output = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
        foreach (var segFile in segmentFiles)
        {
            if (!File.Exists(segFile)) continue;
            using var input = new FileStream(segFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            await input.CopyToAsync(output, ct);
        }
    }

    /// <summary>
    /// Safely deletes a file if it exists, no exceptions.
    /// </summary>
    public static void SafeDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Ignore */ }
    }

    /// <summary>
    /// Ensures a directory exists, creating it if necessary.
    /// </summary>
    public static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Formats a byte count into a human-readable string.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:F2} {suffixes[i]}";
    }

    /// <summary>
    /// Formats a speed (bytes/sec) into a human-readable string.
    /// </summary>
    public static string FormatSpeed(double bytesPerSec)
    {
        return $"{FormatSize((long)bytesPerSec)}/s";
    }

    /// <summary>
    /// Formats a timespan into a concise string.
    /// </summary>
    public static string FormatTimeLeft(TimeSpan? timeLeft)
    {
        if (timeLeft == null) return "Unknown";
        var ts = timeLeft.Value;
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
