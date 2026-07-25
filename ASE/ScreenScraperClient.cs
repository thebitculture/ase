#nullable enable

using ASE.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static ASE.Models.ScreenScraper;

namespace ASE
{
    /// <summary>
    /// Outcome of a ScreenScraper API call: the deserialized payload (when any) plus the
    /// HTTP status code, so callers can tell "not found" (404) apart from quota/auth issues.
    /// </summary>
    public class ScreenScraperResult<T>
    {
        public ScreenScraperResult(HttpStatusCode statusCode, T? data, string? error = null)
        {
            StatusCode = statusCode;
            Data = data;
            Error = error;
        }

        public HttpStatusCode StatusCode { get; }

        /// <summary>Payload; null when the call failed or found nothing.</summary>
        public T? Data { get; }

        /// <summary>API error text: plain-text body on HTTP errors, header.error on JSON failures.</summary>
        public string? Error { get; }

        public bool Success => Data is not null;

        /// <summary>404: the requested game/rom is unknown to ScreenScraper.</summary>
        public bool NotFound => StatusCode == HttpStatusCode.NotFound;

        /// <summary>429 (requests/min), 430 (daily scrape quota) or 431 (daily KO quota) exhausted.</summary>
        public bool QuotaExceeded => (int)StatusCode is 429 or 430 or 431;
    }

    public class ScreenScraperClient
    {
        private readonly HttpClient _http;

        public ScreenScraperClient()
        {
            _http = new HttpClient();
        }

        public static string? PickName(List<ScreenScraper.RegionText>? noms)
        {
            if (noms == null)
                return null;

            string[] preferred = { "eu", "wor", "ss", "us" };

            foreach (var region in preferred)
            {
                var match = noms.Find(n => n.Region == region && !string.IsNullOrEmpty(n.Text));
                if (match != null)
                    return match.Text;
            }

            // Any region as a last resort.
            return noms.Find(n => !string.IsNullOrEmpty(n.Text))?.Text;
        }

        private Dictionary<string, string> BaseParams(bool DebugMode = false)
        {
            var p = new Dictionary<string, string>
            {
                ["devid"] = BuildCredentials.DevId,
                ["devpassword"] = DebugMode ? BuildCredentials.SsDevDebugPassword : BuildCredentials.DevPassword,
                ["softname"] = BuildCredentials.SsDevApp,
                ["output"] = "json"
            };

            // Only send user credentials when configured; empty values are pointless.
            if (!string.IsNullOrWhiteSpace(Config.ConfigOptions.RunninConfig.ScreenScraperUser))
            {
                p["ssid"] = Config.ConfigOptions.RunninConfig.ScreenScraperUser!;
                // Debug mode is not implemented yet
                p["sspassword"] = Config.ConfigOptions.RunninConfig.ScreenScraperPasswordRaw ?? string.Empty;
            }

            return p;
        }

        public async Task<ScreenScraperResult<Jeu>> GetGameAsync(int systemId, string romFileName, long romSize, string sha1, CancellationToken ct = default, bool DebugMode = false)
        {
            var p = BaseParams(DebugMode);
            p["systemeid"] = systemId.ToString(CultureInfo.InvariantCulture);
            p["romtype"] = "rom";
            p["romnom"] = romFileName;
            p["romtaille"] = romSize.ToString(CultureInfo.InvariantCulture);
            p["sha1"] = sha1;

            var url = "https://api.screenscraper.fr/api2/jeuInfos.php?"
                      + string.Join("&", p.Select(kv =>
                          $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            // The API returns 404 for "game not found" and 429/430/431 for quota issues,
            // with a plain-text explanation as body instead of JSON.
            if (!resp.IsSuccessStatusCode)
                return new ScreenScraperResult<Jeu>(resp.StatusCode, null, body.Trim());

            var parsed = JsonSerializer.Deserialize<JeuInfosResponse>(body);

            if (parsed?.Header?.IsSuccess != true)
                return new ScreenScraperResult<Jeu>(resp.StatusCode, null, parsed?.Header?.Error);

            return new ScreenScraperResult<Jeu>(resp.StatusCode, parsed.Response?.Jeu);
        }

        public async Task<ScreenScraperResult<List<Systeme>>> GetSystemsAsync(CancellationToken ct = default, bool DebugMode = false)
        {
            var p = BaseParams(DebugMode);

            var url = "https://api.screenscraper.fr/api2/systemesListe.php?"
                      + string.Join("&", p.Select(kv =>
                          $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            // Same convention as jeuInfos: 4xx for auth/quota issues, plain-text body.
            if (!resp.IsSuccessStatusCode)
                return new ScreenScraperResult<List<Systeme>>(resp.StatusCode, null, body.Trim());

            var parsed = JsonSerializer.Deserialize<SystemesListeResponse>(body);

            if (parsed?.Header?.IsSuccess != true)
                return new ScreenScraperResult<List<Systeme>>(resp.StatusCode, null, parsed?.Header?.Error);

            return new ScreenScraperResult<List<Systeme>>(resp.StatusCode, parsed.Response?.Systemes);
        }

        /// <summary>
        /// Verifies the configured ScreenScraper credentials and returns the account status
        /// (daily request quota used/available, thread limits...). Meant to be called before
        /// starting a scan, so invalid credentials or an already-exhausted quota are caught
        /// without touching any file first.
        /// </summary>
        public async Task<ScreenScraperResult<SsUser>> GetUserInfosAsync(CancellationToken ct = default, bool DebugMode = false)
        {
            var p = BaseParams(DebugMode);

            var url = "https://api.screenscraper.fr/api2/ssuserInfos.php?"
                      + string.Join("&", p.Select(kv =>
                          $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            // Same convention as jeuInfos: 4xx for auth/quota issues, plain-text body.
            if (!resp.IsSuccessStatusCode)
                return new ScreenScraperResult<SsUser>(resp.StatusCode, null, body.Trim());

            var parsed = JsonSerializer.Deserialize<UserInfosResponse>(body);

            if (parsed?.Header?.IsSuccess != true)
                return new ScreenScraperResult<SsUser>(resp.StatusCode, null, parsed?.Header?.Error);

            return new ScreenScraperResult<SsUser>(resp.StatusCode, parsed.Response?.SsUser);
        }

        /// <summary>
        /// Downloads a media file (the <see cref="Media.Url"/> of a game media entry, which
        /// already embeds the API credentials) to <paramref name="destinationPath"/>.
        /// Returns the destination path as payload on success.
        /// </summary>
        public async Task<ScreenScraperResult<string>> DownloadMediaAsync(string url, string destinationPath, CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return new ScreenScraperResult<string>(resp.StatusCode, null, body.Trim());
            }

            // The media endpoints answer 200 with a short text body ("NOMEDIA", "CRCOK", ..)
            // when the media doesn't exist; actual media comes as an image/video content type.
            if (resp.Content.Headers.ContentType?.MediaType?.StartsWith("text/") == true)
            {
                var text = (await resp.Content.ReadAsStringAsync(ct)).Trim();
                return new ScreenScraperResult<string>(HttpStatusCode.NotFound, null, text);
            }

            try
            {
                // Stream straight to disk; normalized videos can be tens of MB.
                await using var file = File.Create(destinationPath);
                await resp.Content.CopyToAsync(file, ct);
            }
            catch
            {
                // Don't leave a truncated file behind on cancellation or I/O failure.
                if (File.Exists(destinationPath))
                    File.Delete(destinationPath);
                throw;
            }

            return new ScreenScraperResult<string>(resp.StatusCode, destinationPath);
        }
    }
}
