using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASE
{
    public static class ReleaseChecker
    {
        public class ReleaseInfoModel
        {
            public bool ExistsNewVersion { get; set; } = false;

            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = "";

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("html_url")]
            public string HtmlUrl { get; set; } = "";

            [JsonPropertyName("published_at")]
            public DateTime PublishedAt { get; set; }

            [JsonPropertyName("prerelease")]
            public bool IsPreRelease { get; set; }
        }

        public static ReleaseInfoModel ReleaseInfo = null;

        public static async Task IsNewVersionAvailableAsync()
        {
            try
            {
                HttpClient _httpClient;

                _httpClient = new HttpClient();
                _httpClient.DefaultRequestHeaders.Add("User-Agent", $"ASE/{Config.Version}");

                var url = $"https://api.github.com/repos/thebitculture/ASE/releases/latest";

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                ReleaseInfo = JsonSerializer.Deserialize<ReleaseInfoModel>(json);

                var latestVersion = ReleaseInfo.TagName.TrimStart('v');
                ReleaseInfo.ExistsNewVersion = Version.Parse(latestVersion) > Version.Parse(Config.Version);
            }
            catch (HttpRequestException httpex)
            {
                ColoredConsole.WriteLine($"Error querying Github for new version: {httpex.Message}", Config.ConfigOptions.DebugModes.Information);
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"Error checking for new version: {ex.Message}", Config.ConfigOptions.DebugModes.Information);
            }
        }
    }
}
