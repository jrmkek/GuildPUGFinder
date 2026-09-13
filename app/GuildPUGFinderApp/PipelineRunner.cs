// PipelineRunner.cs
using System.IO;
using System.Collections.Concurrent;

namespace GuildPUGFinderApp;

public class PipelineRunner
{
    private readonly Config _config;

    // Session-lifetime cache (survives across separate Run/Watch triggers,
    // since a new PipelineRunner is created each time but this is static).
    // ConcurrentDictionary since multiple candidates are now processed in
    // parallel and can hit this at the same time.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);
    private static readonly ConcurrentDictionary<string, (CharacterResult Result, DateTime FetchedAt)> _cache = new();

    // TBC role capability - a class either can or can't fill a role, no
    // ambiguity, so skipping is safe and never loses real data.
    private static readonly HashSet<string> TankCapableClasses = new(StringComparer.OrdinalIgnoreCase) { "Warrior", "Druid", "Paladin" };
    private static readonly HashSet<string> HealCapableClasses = new(StringComparer.OrdinalIgnoreCase) { "Priest", "Druid", "Paladin", "Shaman" };

    // How many candidates get processed at once. Each candidate can itself
    // fire up to 4 sequential queries (Overall/DPS/Heal/Tank), so this
    // isn't the same as "max concurrent HTTP calls" - it's roughly
    // MaxConcurrentCandidates x (up to 4) in flight at a time. Kept modest
    // to avoid hammering WCL or tripping any per-connection limits.
    private const int MaxConcurrentCandidates = 6;

    public PipelineRunner(Config config)
    {
        _config = config;
    }

    // onRow is called once per candidate as results come in (from
    // whichever worker finishes it - not guaranteed to be in pending's
    // original order, since candidates now run concurrently). A UI can
    // still update a live list the same way as before.
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

        var rows = new ConcurrentBag<CandidateRow>();
        var eligible = new ConcurrentDictionary<string, object?>();

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

        async Task ProcessCandidateAsync(string name, object? candidateData)
        {
            if (options.BlacklistedNames.Contains(name)) return;

            string? preKnownClass = candidateData is Dictionary<string, object?> data
                && data.TryGetValue("className", out var cn) ? cn as string : null;

            if (options.AllowedClasses.Count > 0 && preKnownClass != null
                && !options.AllowedClasses.Contains(preKnownClass))
            {
                return; // skip entirely - no query, no row
            }

            bool canTank = preKnownClass == null || TankCapableClasses.Contains(preKnownClass);
            bool canHeal = preKnownClass == null || HealCapableClasses.Contains(preKnownClass);

            // The 4 categories for THIS candidate still run one after
            // another (sequential awaits) - it's candidates themselves
            // that run in parallel with each other via the semaphore below.
            CharacterResult? overallResult = options.QueryOverall ? await FetchCachedAsync(name, null) : null;
            CharacterResult? dpsResult = options.QueryDps ? await FetchCachedAsync(name, "DPS") : null;
            CharacterResult? healResult = (options.QueryHeal && canHeal) ? await FetchCachedAsync(name, "Healer") : null;
            CharacterResult? tankResult = (options.QueryTank && canTank) ? await FetchCachedAsync(name, "Tank") : null;

            double? overallParse = overallResult?.BestParsePercent;
            double? dpsParse = dpsResult?.BestParsePercent;
            double? healParse = healResult?.BestParsePercent;
            double? tankParse = tankResult?.BestParsePercent;

            double? bestOfAll = new[] { overallParse, dpsParse, healParse, tankParse }
                .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty().Max() is var m && m > 0 ? m : (double?)null;

            var queried = new[] { overallResult, dpsResult, healResult, tankResult }.Where(r => r != null).Select(r => r!).ToList();

            var dataSource =
                bestOfAll.HasValue && overallResult != null && bestOfAll == overallParse ? overallResult :
                bestOfAll.HasValue && dpsResult != null && bestOfAll == dpsParse ? dpsResult :
                bestOfAll.HasValue && healResult != null && bestOfAll == healParse ? healResult :
                bestOfAll.HasValue && tankResult != null && bestOfAll == tankParse ? tankResult :
                queried.FirstOrDefault(r => r.Found) ?? queried.FirstOrDefault();

            if (dataSource == null) return;

            string? effectiveClassName = preKnownClass ?? dataSource.ClassName;

            CandidateStatus status;

            if (dataSource.RawError == "character not found (null)")
            {
                status = CandidateStatus.NotFound;
            }
            else if (!dataSource.Found && dataSource.RawError != null && !dataSource.RawError.StartsWith("no zoneRankings"))
            {
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

        using var gate = new SemaphoreSlim(MaxConcurrentCandidates);
        var tasks = pending.Select(async kv =>
        {
            await gate.WaitAsync();
            try { await ProcessCandidateAsync(kv.Key, kv.Value); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        table["eligible"] = new Dictionary<string, object?>(eligible);
        var newLuaText = LuaTableParser.Serialize(varName, table);
        File.WriteAllText(_config.SavedVariablesPath, newLuaText);

        return rows.ToList();
    }
}