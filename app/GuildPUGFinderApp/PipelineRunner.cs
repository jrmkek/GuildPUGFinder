// PipelineRunner.cs
using System.IO;

namespace GuildPUGFinderApp;

public class PipelineRunner
{
    private readonly Config _config;

    // Session-lifetime cache (survives across separate Run/Watch triggers,
    // since a new PipelineRunner is created each time but this is static).
    // Keyed by everything that affects the result, so different filters
    // don't collide.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);
    private static readonly Dictionary<string, (CharacterResult Result, DateTime FetchedAt)> _cache = new();

    // TBC role capability - a class either can or can't fill a role, no
    // ambiguity, so skipping is safe and never loses real data.
    private static readonly HashSet<string> TankCapableClasses = new(StringComparer.OrdinalIgnoreCase) { "Warrior", "Druid", "Paladin" };
    private static readonly HashSet<string> HealCapableClasses = new(StringComparer.OrdinalIgnoreCase) { "Priest", "Druid", "Paladin", "Shaman" };

    public PipelineRunner(Config config)
    {
        _config = config;
    }

    // onRow is called once per candidate as results come in, so a UI can
    // update a live list instead of waiting for the whole batch.
    public async Task<List<CandidateRow>> RunAsync(RunOptions options, Action<CandidateRow>? onRow = null, bool forceFresh = false)
    {
        if (!File.Exists(_config.SavedVariablesPath))
            throw new FileNotFoundException($"SavedVariables file not found at: {_config.SavedVariablesPath}");

        var luaText = File.ReadAllText(_config.SavedVariablesPath);
        var (varName, table) = LuaTableParser.ParseAssignment(luaText);

        if (!table.TryGetValue("pending", out var pendingObj) || pendingObj is not Dictionary<string, object?> pending)
            return new List<CandidateRow>();

        var client = new WarcraftLogsClient(_config.ClientId, _config.ClientSecret, _config.Site);
        await client.AuthenticateAsync();

        var rateLimit = await client.GetRateLimitAsync();
        if (rateLimit.HasValue)
        {
            var (limit, spent, resetInSeconds) = rateLimit.Value;
            double remaining = limit - spent;
            // Rough safety margin: cost per candidate now depends on how
            // many categories are checked (1-4 queries) - if we're already
            // near the cap, bail loudly instead of burning through and
            // returning a wall of misleading NoData results.
            int queriesPerCandidate = (options.QueryOverall ? 1 : 0) + (options.QueryDps ? 1 : 0)
                + (options.QueryHeal ? 1 : 0) + (options.QueryTank ? 1 : 0);
            if (remaining < pending.Count * Math.Max(queriesPerCandidate, 1) * 1.5)
            {
                int minutes = resetInSeconds / 60;
                throw new Exception(
                    $"WCL rate limit nearly exhausted: {spent:F0}/{limit:F0} points used this hour, " +
                    $"resets in ~{minutes} min. Skipping this run rather than returning bad data - try again after the reset.");
            }
        }

        var rows = new List<CandidateRow>();
        var eligible = new Dictionary<string, object?>();

        // Raw API responses get logged here so you can inspect exactly
        // what WCL sent back, not just the computed pass/fail numbers.
        string logDir = Path.Combine(AppContext.BaseDirectory, "logs", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(logDir);

        void WriteLog(string name, string role, string? json)
        {
            if (json == null) return;
            try
            {
                var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
                File.WriteAllText(Path.Combine(logDir, $"{safeName}_{role}.json"), json);
            }
            catch { /* best-effort - logging never breaks the actual run */ }
        }

        async Task<CharacterResult> FetchCachedAsync(string name, string? role)
        {
            string cacheKey = $"{_config.ServerSlug}|{_config.ServerRegion}|{name}|{role ?? "overall"}|{options.ZoneId}|{options.Partition}";
            if (!forceFresh && _cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
            {
                return cached.Result; // no query spent, no new log written
            }

            var result = await client.GetCharacterAsync(name, _config.ServerSlug, _config.ServerRegion, options.Partition, options.ZoneId, role);
            _cache[cacheKey] = (result, DateTime.UtcNow);
            WriteLog(name, role ?? "overall", result.RawResponseJson);
            return result;
        }

        foreach (var (name, candidateData) in pending)
        {
            if (options.BlacklistedNames.Contains(name))
            {
                continue; // blacklisted - no query, no row, ever
            }

            // Class is captured client-side by the addon (via
            // GetSearchResultMemberInfo) and stored per-candidate. If we
            // have it and it doesn't match the selected filter, skip the
            // WCL query entirely. If it's missing (older scan, or the
            // addon-side lookup failed), fall back to querying anyway
            // since we can't know the class ahead of time otherwise.
            string? preKnownClass = candidateData is Dictionary<string, object?> data
                && data.TryGetValue("className", out var cn) ? cn as string : null;

            if (options.AllowedClasses.Count > 0 && preKnownClass != null
                && !options.AllowedClasses.Contains(preKnownClass))
            {
                continue; // skip entirely - no query, no row
            }

            // Query only the categories the user checked - skipping one
            // entirely saves rate-limit budget, not just hides a column.
            // Also skip Heal/Tank automatically when the class flat-out
            // can't fill that role in TBC (e.g. Mage can never tank) -
            // this is a hard game-mechanics fact, not a guess, so it never
            // loses real data. Falls back to querying anyway if class is
            // unknown (older scan / addon lookup failed).
            bool canTank = preKnownClass == null || TankCapableClasses.Contains(preKnownClass);
            bool canHeal = preKnownClass == null || HealCapableClasses.Contains(preKnownClass);

            CharacterResult? overallResult = options.QueryOverall ? await FetchCachedAsync(name, null) : null;
            CharacterResult? dpsResult = options.QueryDps ? await FetchCachedAsync(name, "DPS") : null;
            CharacterResult? healResult = (options.QueryHeal && canHeal) ? await FetchCachedAsync(name, "Healer") : null;
            CharacterResult? tankResult = (options.QueryTank && canTank) ? await FetchCachedAsync(name, "Tank") : null;

            double? overallParse = overallResult?.BestParsePercent;
            double? dpsParse = dpsResult?.BestParsePercent;
            double? healParse = healResult?.BestParsePercent;
            double? tankParse = tankResult?.BestParsePercent;

            // Best-of and the data source (ilvl/spec/class/per-boss) are
            // computed only from categories that were actually queried -
            // if you only checked Healer, that's the only number that can
            // win, and it's also where ilvl/spec come from.
            double? bestOfAll = new[] { overallParse, dpsParse, healParse, tankParse }
                .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty().Max() is var m && m > 0 ? m : (double?)null;

            var queried = new[] { overallResult, dpsResult, healResult, tankResult }.Where(r => r != null).Select(r => r!).ToList();

            var dataSource =
                bestOfAll.HasValue && overallResult != null && bestOfAll == overallParse ? overallResult :
                bestOfAll.HasValue && dpsResult != null && bestOfAll == dpsParse ? dpsResult :
                bestOfAll.HasValue && healResult != null && bestOfAll == healParse ? healResult :
                bestOfAll.HasValue && tankResult != null && bestOfAll == tankParse ? tankResult :
                queried.FirstOrDefault(r => r.Found) ?? queried.FirstOrDefault();

            if (dataSource == null)
            {
                // Nothing was checked at all - nothing to do for this
                // candidate. Shouldn't normally happen since the UI should
                // prevent zero categories selected, but guard anyway.
                continue;
            }

            string? effectiveClassName = preKnownClass ?? dataSource.ClassName;

            CandidateStatus status;

            if (dataSource.RawError == "character not found (null)")
            {
                status = CandidateStatus.NotFound;
            }
            else if (!dataSource.Found && dataSource.RawError != null && !dataSource.RawError.StartsWith("no zoneRankings"))
            {
                // A real failure (bad query, auth problem, HTTP error, etc.)
                // - surface it instead of silently lumping it in with NoData.
                status = CandidateStatus.Error;
            }
            else if (!bestOfAll.HasValue || !dataSource.AverageItemLevel.HasValue)
            {
                status = CandidateStatus.NoData;
            }
            else
            {
                bool meetsOverall = bestOfAll >= options.MinBestParsePercent
                    && dataSource.AverageItemLevel >= options.MinAverageItemLevel;

                bool meetsClass = options.AllowedClasses.Count == 0
                    || (effectiveClassName != null && options.AllowedClasses.Contains(effectiveClassName));

                bool meetsPerBoss = !options.UsePerBossParse
                    || (dataSource.PerBoss.Count > 0 && dataSource.PerBoss.All(b => b.RankPercent >= options.MinPerBossParsePercent));

                status = (meetsOverall && meetsClass && meetsPerBoss) ? CandidateStatus.Pass : CandidateStatus.Fail;
            }

            var row = new CandidateRow(
                name, status, overallParse, dpsParse, healParse, tankParse, dataSource.AverageItemLevel,
                dataSource.Spec, effectiveClassName, dataSource.PerBoss, dataSource.RawError);

            rows.Add(row);
            onRow?.Invoke(row);

            if (status == CandidateStatus.Pass)
            {
                eligible[name] = new Dictionary<string, object?>
                {
                    ["overallParsePercent"] = overallParse,
                    ["dpsParsePercent"] = dpsParse,
                    ["healParsePercent"] = healParse,
                    ["tankParsePercent"] = tankParse,
                    ["averageItemLevel"] = dataSource.AverageItemLevel,
                    ["spec"] = dataSource.Spec,
                    ["className"] = effectiveClassName,
                };
            }
        }

        table["eligible"] = eligible;
        var newLuaText = LuaTableParser.Serialize(varName, table);
        File.WriteAllText(_config.SavedVariablesPath, newLuaText);

        return rows;
    }
}