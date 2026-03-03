using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Win32;

namespace MyDM.App.Utilities;

public sealed record NativeHostRegistrationResult(
    string ChromiumManifestPath,
    string FirefoxManifestPath,
    int ChromiumRegistryKeysWritten,
    bool FirefoxRegistryWritten,
    IReadOnlyList<string> ChromiumExtensionIds,
    IReadOnlyList<string> FirefoxExtensionIds);

public static class NativeHostRegistrationService
{
    private const string HostName = "com.mydm.native";
    private const string HostDescription = "MyDM Native Messaging Host";
    private static readonly Regex ChromiumIdRegex = new("^[a-p]{32}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FirefoxIdRegex = new("^[A-Za-z0-9._@+-]{3,}$", RegexOptions.Compiled);

    private static readonly string[] ChromiumRegistryPaths =
    {
        @"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.mydm.native",
        @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.mydm.native",
        @"SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.mydm.native",
        @"SOFTWARE\Vivaldi\NativeMessagingHosts\com.mydm.native",
        @"SOFTWARE\Opera Software\NativeMessagingHosts\com.mydm.native",
        @"SOFTWARE\Opera Software\Opera Stable\NativeMessagingHosts\com.mydm.native",
        @"SOFTWARE\Opera Software\Opera GX Stable\NativeMessagingHosts\com.mydm.native"
    };

    private const string FirefoxRegistryPath = @"SOFTWARE\Mozilla\NativeMessagingHosts\com.mydm.native";

    public static IReadOnlyList<string> ParseChromiumExtensionIds(string? raw)
    {
        return ParseIds(raw, ChromiumIdRegex, value => value.ToLowerInvariant());
    }

    public static IReadOnlyList<string> ParseFirefoxExtensionIds(string? raw)
    {
        return ParseIds(raw, FirefoxIdRegex, value => value);
    }

    public static NativeHostRegistrationResult Register(
        string hostExecutablePath,
        string manifestDirectory,
        IReadOnlyCollection<string> chromiumExtensionIds,
        IReadOnlyCollection<string> firefoxExtensionIds)
    {
        if (string.IsNullOrWhiteSpace(hostExecutablePath) || !File.Exists(hostExecutablePath))
        {
            throw new FileNotFoundException("MyDM.NativeHost.exe not found.", hostExecutablePath);
        }
        EnsureNativeHostBoots(hostExecutablePath);

        var chromiumIds = chromiumExtensionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var firefoxIds = firefoxExtensionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chromiumIds.Count == 0)
        {
            throw new InvalidOperationException("At least one Chromium extension ID is required.");
        }

        if (firefoxIds.Count == 0)
        {
            throw new InvalidOperationException("At least one Firefox add-on ID is required.");
        }

        Directory.CreateDirectory(manifestDirectory);

        var chromiumManifestPath = Path.Combine(manifestDirectory, "com.mydm.native.chromium.json");
        var firefoxManifestPath = Path.Combine(manifestDirectory, "com.mydm.native.firefox.json");
        var legacyManifestPath = Path.Combine(manifestDirectory, "com.mydm.native.json");
        var options = new JsonSerializerOptions { WriteIndented = true };

        var chromiumManifest = new
        {
            name = HostName,
            description = HostDescription,
            path = hostExecutablePath,
            type = "stdio",
            allowed_origins = chromiumIds.Select(id => $"chrome-extension://{id}/").ToArray()
        };
        var firefoxManifest = new
        {
            name = HostName,
            description = HostDescription,
            path = hostExecutablePath,
            type = "stdio",
            allowed_extensions = firefoxIds.ToArray()
        };

        File.WriteAllText(chromiumManifestPath, JsonSerializer.Serialize(chromiumManifest, options));
        File.WriteAllText(firefoxManifestPath, JsonSerializer.Serialize(firefoxManifest, options));
        // Keep legacy file for compatibility with existing tooling that expects this name.
        File.WriteAllText(legacyManifestPath, JsonSerializer.Serialize(chromiumManifest, options));

        var chromiumKeysWritten = 0;
        foreach (var registryPath in ChromiumRegistryPaths)
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath);
            key?.SetValue(string.Empty, chromiumManifestPath);
            if (key != null)
            {
                chromiumKeysWritten++;
            }
        }

        var firefoxWritten = false;
        using (var key = Registry.CurrentUser.CreateSubKey(FirefoxRegistryPath))
        {
            key?.SetValue(string.Empty, firefoxManifestPath);
            firefoxWritten = key != null;
        }

        return new NativeHostRegistrationResult(
            ChromiumManifestPath: chromiumManifestPath,
            FirefoxManifestPath: firefoxManifestPath,
            ChromiumRegistryKeysWritten: chromiumKeysWritten,
            FirefoxRegistryWritten: firefoxWritten,
            ChromiumExtensionIds: chromiumIds,
            FirefoxExtensionIds: firefoxIds);
    }

    private static void EnsureNativeHostBoots(string hostExecutablePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = hostExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--self-test");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start MyDM.NativeHost.exe.");

        if (!process.WaitForExit(10_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            throw new InvalidOperationException("MyDM.NativeHost self-test timed out.");
        }

        var stderr = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? $"MyDM.NativeHost self-test failed (ExitCode={process.ExitCode})."
                    : $"MyDM.NativeHost self-test failed (ExitCode={process.ExitCode}): {stderr}");
        }
    }

    private static IReadOnlyList<string> ParseIds(
        string? raw,
        Regex pattern,
        Func<string, string> normalize)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var parts = raw
            .Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(normalize)
            .Where(part => pattern.IsMatch(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts;
    }
}
