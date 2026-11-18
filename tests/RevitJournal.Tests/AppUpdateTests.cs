using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using RevitJournal.Core.Live;

namespace RevitJournal.Tests;

/// <summary>Reading GitHub's latest release and checking a downloaded installer; the answers are cut down to their shape.</summary>
public class AppUpdateTests
{
    private const string Prefix = AppUpdate.DownloadPrefix + "v0.8.0/";

    private static string Release(string tag = "v0.8.0", bool prerelease = false, string setupUrl = Prefix + "RevitDoctor-0.8.0-setup.exe") => $$"""
        {
          "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/basepointlabs-io/journal-files/releases/tag/{{tag}}",
          "body": "**Revit Doctor** watches...\n\n## What's new in 0.8.0\n\n### Updates\n- **Install update** from the app.\n\n## Download\n\n| File | For |",
          "assets": [
            { "name": "RevitDoctor-0.8.0-setup.exe", "browser_download_url": "{{setupUrl}}" },
            { "name": "SHA256SUMS", "browser_download_url": "{{Prefix}}SHA256SUMS" }
          ]
        }
        """;

    [Fact]
    public void A_newer_release_is_found_with_its_installer_and_checksums()
    {
        var r = AppUpdate.Parse(Release())!;

        Assert.Equal(new Version(0, 8, 0), r.Version);
        Assert.EndsWith("RevitDoctor-0.8.0-setup.exe", r.SetupUrl);
        Assert.EndsWith("SHA256SUMS", r.ChecksumsUrl);
        Assert.True(AppUpdate.IsNewer(r, new Version(0, 7, 3, 0)));
        Assert.False(AppUpdate.IsNewer(r, new Version(0, 8, 0, 0)));
        Assert.Equal("Updates\n- Install update from the app.", AppUpdate.Summary(r));
    }

    [Fact]
    public void Pre_releases_odd_tags_and_files_from_elsewhere_are_ignored()
    {
        Assert.Null(AppUpdate.Parse(Release(prerelease: true)));
        Assert.Null(AppUpdate.Parse(Release(tag: "nightly")));
        Assert.Null(AppUpdate.Parse(Release(setupUrl: "https://example.com/RevitDoctor-0.8.0-setup.exe"))!.SetupUrl);
    }

    [Fact]
    public async Task The_installer_is_kept_only_when_it_matches_its_checksum()
    {
        byte[] setup = Encoding.ASCII.GetBytes("pretend installer");
        string hash = Convert.ToHexString(SHA256.HashData(setup)).ToLowerInvariant();
        var release = AppUpdate.Parse(Release())!;
        string folder = Path.Combine(Path.GetTempPath(), "rd-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var good = new HttpClient(new Fake(setup, $"{hash}  RevitDoctor-0.8.0-setup.exe\n"));
            string path = await AppUpdate.DownloadSetupAsync(good, release, folder);
            Assert.Equal(setup, File.ReadAllBytes(path));

            using var bad = new HttpClient(new Fake(Encoding.ASCII.GetBytes("something else"), $"{hash}  RevitDoctor-0.8.0-setup.exe\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdate.DownloadSetupAsync(bad, release, folder));
            Assert.False(File.Exists(Path.Combine(folder, "RevitDoctor-0.8.0-setup.exe")));      // a file that does not match is removed
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    private sealed class Fake(byte[] setup, string sums) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal)
                    ? new StringContent(sums) : new ByteArrayContent(setup),
            });
    }
}
