namespace MyDM.Core;

/// <summary>
/// Shared inter-process communication constants used by both NativeHost and WPF App.
/// </summary>
public static class IpcConstants
{
    /// <summary>Named pipe name for NativeHost → WPF App communication.</summary>
    public const string PipeName = "MyDM_DownloadIPC";

    /// <summary>Signal that a new download was started. Format: "DOWNLOAD_STARTED|{downloadId}"</summary>
    public const string DownloadStarted = "DOWNLOAD_STARTED";

    /// <summary>Build a download-started signal message.</summary>
    public static string MakeDownloadStartedMessage(string downloadId) => $"{DownloadStarted}|{downloadId}";

    /// <summary>Try to parse a pipe message into (signal, downloadId).</summary>
    public static bool TryParse(string message, out string signal, out string downloadId)
    {
        signal = "";
        downloadId = "";
        if (string.IsNullOrWhiteSpace(message)) return false;

        var parts = message.Split('|', 2);
        if (parts.Length < 2) return false;

        signal = parts[0];
        downloadId = parts[1];
        return !string.IsNullOrWhiteSpace(downloadId);
    }
}
