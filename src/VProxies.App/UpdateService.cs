using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VProxies;

public sealed record AvailableUpdate(Version Version, string Tag, string InstallerName, Uri InstallerUrl, Uri ChecksumUrl);

public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/vproxies20/vproxies-windows/releases/latest";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VProxies-Windows", CurrentVersion.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(LatestReleaseApi, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty release response.");

        var versionText = release.TagName.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        if (!Version.TryParse(versionText, out var parsed)) throw new InvalidOperationException("The latest GitHub release has an invalid version tag.");
        var latest = Normalize(parsed);
        if (latest <= CurrentVersion) return null;

        var installer = release.Assets.FirstOrDefault(asset => asset.Name.StartsWith("VProxiesSetup-", StringComparison.OrdinalIgnoreCase) && asset.Name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase));
        if (installer is null) throw new InvalidOperationException("The latest release does not contain a Windows installer.");
        var checksum = release.Assets.FirstOrDefault(asset => asset.Name.Equals(installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (checksum is null) throw new InvalidOperationException("The latest release does not contain the installer checksum.");

        return new AvailableUpdate(latest, release.TagName, installer.Name, ValidateDownloadUrl(installer.DownloadUrl), ValidateDownloadUrl(checksum.DownloadUrl));
    }

    public async Task<string> DownloadAndVerifyAsync(AvailableUpdate update, CancellationToken cancellationToken = default)
    {
        var checksumText = await _http.GetStringAsync(update.ChecksumUrl, cancellationToken);
        var expected = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The release checksum file is invalid.");

        var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VProxies", "Updates");
        Directory.CreateDirectory(updateDirectory);
        var finalPath = Path.Combine(updateDirectory, update.InstallerName);
        var temporaryPath = finalPath + ".download";
        try
        {
            using var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(destination, cancellationToken);

            await using var installerStream = File.OpenRead(temporaryPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(installerStream, cancellationToken));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded update failed SHA-256 verification and was deleted.");

            File.Move(temporaryPath, finalPath, true);
            return finalPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static Uri ValidateDownloadUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub returned an untrusted update download URL.");
        return uri;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
}
