// WarcraftLogsClient.cs
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GuildPUGFinderApp;

public record BossParse(string EncounterName, double RankPercent);

public record CharacterResult(
    string Name,
    bool Found,
    double? BestParsePercent,
    double? MedianParsePercent,
    double? AverageItemLevel,
    string? Spec,
    int? ClassId,
    string? ClassName,
    List<BossParse> PerBoss,
    string? RawError,
    string? RawResponseJson = null
);

public class WarcraftLogsClient
{
    private readonly HttpClient _http = new();
    private readonly string _tokenUrl;
    private readonly string _apiUrl;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private string? _accessToken;
    private Dictionary<int, string>? _classNamesById;

    public WarcraftLogsClient(string clientId, string clientSecret, string site = "fresh")
    {
        _clientId = clientId;
        _clientSecret = clientSecret;

        string baseDomain = site switch
        {
            "classic" => "classic.warcraftlogs.com",
            "www" or "retail" => "www.warcraftlogs.com",
            _ => "fresh.warcraftlogs.com"
        };

        _tokenUrl = $"https://{baseDomain}/oauth/token";
        _apiUrl = $"https://{baseDomain}/api/v2/client";
    }

    public async Task AuthenticateAsync()
    {
        var authBytes = System.Text.Encoding.ASCII.GetBytes($"{_clientId}:{_clientSecret}");
        var authHeader = Convert.ToBase64String(authBytes);

        var request = new HttpRequestMessage(HttpMethod.Post, _tokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Token request failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
    }

    // Fetches the id -> name class mapping directly from WCL's own schema,
    // rather than trusting a hardcoded table that may not match this site's
    // enum (retail, classic, and fresh have shown small inconsistencies
    // elsewhere in this API, so don't assume - ask it directly).
    // Degrades gracefully (empty dict) if this query's shape turns out to be
    // wrong on this site, rather than crashing the whole run.
    public async Task<Dictionary<int, string>> GetClassNamesAsync()
    {
        if (_classNamesById != null) return _classNamesById;
        _classNamesById = new Dictionary<int, string>();

        if (_accessToken == null)
            throw new InvalidOperationException("Call AuthenticateAsync() first.");

        const string query = @"
query GetClasses {
  gameData {
    classes {
      id
      name
    }
  }
}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = JsonContent.Create(new { query });

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out _)) return _classNamesById; // degrade gracefully

            var classes = root.GetProperty("data").GetProperty("gameData").GetProperty("classes");
            foreach (var c in classes.EnumerateArray())
            {
                int id = c.GetProperty("id").GetInt32();
                string name = c.GetProperty("name").GetString() ?? "";
                _classNamesById[id] = name;
            }
        }
        catch
        {
            // leave _classNamesById empty; class filtering degrades to
            // "unknown" rather than crashing the run.
        }

        return _classNamesById;
    }

    public async Task<(double LimitPerHour, double PointsSpent, int ResetInSeconds)?> GetRateLimitAsync()
    {
        if (_accessToken == null)
            throw new InvalidOperationException("Call AuthenticateAsync() first.");

        const string query = @"
query GetRateLimit {
  rateLimitData {
    limitPerHour
    pointsSpentThisHour
    pointsResetIn
  }
}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = JsonContent.Create(new { query });

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out _)) return null;

            var rl = root.GetProperty("data").GetProperty("rateLimitData");
            return (
                rl.GetProperty("limitPerHour").GetDouble(),
                rl.GetProperty("pointsSpentThisHour").GetDouble(),
                rl.GetProperty("pointsResetIn").GetInt32()
            );
        }
        catch
        {
            return null; // degrade gracefully - just skip the pre-flight check
        }
    }

    // Fetches the raid tier list directly from WCL (id + name), so the user
    // can find the right zoneID for P1 (Karazhan/Gruul/Mag), P2 (SSC/TK),
    // etc without guessing. Schema/field names here are a best guess at
    // WCL's typical "worldData.zones" shape - unverified until tested live.
    public async Task<(List<(int Id, string Name)> Zones, string? Error)> GetZonesAsync()
    {
        if (_accessToken == null)
            throw new InvalidOperationException("Call AuthenticateAsync() first.");

        const string query = @"
query GetZones {
  worldData {
    zones {
      id
      name
    }
  }
}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = JsonContent.Create(new { query });

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errors))
                return (new(), errors.ToString());

            var zones = new List<(int, string)>();
            var zonesEl = root.GetProperty("data").GetProperty("worldData").GetProperty("zones");
            foreach (var z in zonesEl.EnumerateArray())
            {
                zones.Add((z.GetProperty("id").GetInt32(), z.GetProperty("name").GetString() ?? ""));
            }
            return (zones, null);
        }
        catch (Exception ex)
        {
            return (new(), ex.Message);
        }
    }

    // role: "DPS" | "Healer" | "Tank" | null (null = WCL's own default).
    // Added so callers can query the same character under each role and
    // see which one(s) actually have real logged data.
    public async Task<CharacterResult> GetCharacterAsync(string name, string serverSlug, string serverRegion, int? partition = null, int? zoneId = null, string? role = null)
    {
        if (_accessToken == null)
            throw new InvalidOperationException("Call AuthenticateAsync() first.");

        var classNames = await GetClassNamesAsync();

        // partition = content-phase segmentation within one raid tier.
        // zoneId = which raid tier entirely (Karazhan vs SSC/TK vs BT/Hyjal
        // etc - these are different WCL "zones", found via GetZonesAsync).
        // Both are optional and combinable; omitting both matches the
        // original all-time-aggregate-on-latest-zone behavior.
        var variables = new Dictionary<string, object> { ["name"] = name, ["serverSlug"] = serverSlug, ["serverRegion"] = serverRegion };
        var varDecls = new List<string> { "$name: String!", "$serverSlug: String!", "$serverRegion: String!" };
        var zoneRankingsArgs = new List<string>();

        if (partition.HasValue)
        {
            variables["partition"] = partition.Value;
            varDecls.Add("$partition: Int!");
            zoneRankingsArgs.Add("partition: $partition");
        }
        if (zoneId.HasValue)
        {
            variables["zoneID"] = zoneId.Value;
            varDecls.Add("$zoneID: Int!");
            zoneRankingsArgs.Add("zoneID: $zoneID");
        }
        if (!string.IsNullOrEmpty(role))
        {
            variables["role"] = role;
            varDecls.Add("$role: RoleType!");
            zoneRankingsArgs.Add("role: $role");
        }

        string zoneRankingsField = zoneRankingsArgs.Count > 0
            ? $"zoneRankings({string.Join(", ", zoneRankingsArgs)})"
            : "zoneRankings";

        string query = $@"
query GetCharacter({string.Join(", ", varDecls)}) {{
  characterData {{
    character(name: $name, serverSlug: $serverSlug, serverRegion: $serverRegion) {{
      name
      classID
      {zoneRankingsField}
    }}
  }}
}}";

        var payload = new { query, variables };

        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = JsonContent.Create(payload);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return new CharacterResult(name, false, null, null, null, null, null, null, new(), $"HTTP {(int)response.StatusCode}: {body}", body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errors))
            return new CharacterResult(name, false, null, null, null, null, null, null, new(), errors.ToString(), body);

        var character = root.GetProperty("data").GetProperty("characterData").GetProperty("character");
        if (character.ValueKind == JsonValueKind.Null)
            return new CharacterResult(name, false, null, null, null, null, null, null, new(), "character not found (null)", body);

        int? classId = character.TryGetProperty("classID", out var cid) && cid.ValueKind == JsonValueKind.Number
            ? cid.GetInt32() : null;
        string? className = classId.HasValue && classNames.TryGetValue(classId.Value, out var cn) ? cn : null;

        if (!character.TryGetProperty("zoneRankings", out var zoneRankings) || zoneRankings.ValueKind == JsonValueKind.Null)
        {
            var raw = character.GetRawText();
            if (raw.Length > 500) raw = raw[..500] + "...(truncated)";
            return new CharacterResult(name, false, null, null, null, null, classId, className, new(), $"no zoneRankings - raw character JSON: {raw}", body);
        }

        double? best = zoneRankings.TryGetProperty("bestPerformanceAverage", out var b) && b.ValueKind == JsonValueKind.Number
            ? b.GetDouble() : null;
        double? median = zoneRankings.TryGetProperty("medianPerformanceAverage", out var m) && m.ValueKind == JsonValueKind.Number
            ? m.GetDouble() : null;

        double? avgIlvl = null;
        string? spec = null;
        var perBoss = new List<BossParse>();

        if (zoneRankings.TryGetProperty("rankings", out var rankings) && rankings.ValueKind == JsonValueKind.Array)
        {
            var ilvls = new List<double>();
            foreach (var ranking in rankings.EnumerateArray())
            {
                if (spec == null && ranking.TryGetProperty("spec", out var specEl) && specEl.ValueKind == JsonValueKind.String)
                    spec = specEl.GetString();

                if (ranking.TryGetProperty("bestRank", out var bestRank) && bestRank.ValueKind == JsonValueKind.Object
                    && bestRank.TryGetProperty("ilvl", out var ilvlEl) && ilvlEl.ValueKind == JsonValueKind.Number)
                {
                    ilvls.Add(ilvlEl.GetDouble());
                }

                if (ranking.TryGetProperty("encounter", out var encEl) && encEl.ValueKind == JsonValueKind.Object
                    && encEl.TryGetProperty("name", out var encName)
                    && ranking.TryGetProperty("rankPercent", out var rp) && rp.ValueKind == JsonValueKind.Number)
                {
                    perBoss.Add(new BossParse(encName.GetString() ?? "?", rp.GetDouble()));
                }
            }
            if (ilvls.Count > 0) avgIlvl = ilvls.Average();
        }

        return new CharacterResult(name, true, best, median, avgIlvl, spec, classId, className, perBoss, null, body);
    }
}