// PipelineRunner.cs
using System.IO;

namespace GuildPUGFinderApp;

public class PipelineRunner
{
    private readonly Config _config;

    public PipelineRunner(Config config)
    {
        _config = config;
    }

    // onRow is called once per candidate as results come in, so a UI can
    // update a live list instead of waiting for the whole batch.
    public async Task<List<CandidateRow>> RunAsync(RunOptions options, Action<CandidateRow>? onRow = null)
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
            // Rough safety margin: assume each candidate costs a handful of
            // points (exact per-query cost isn't documented) - if we're
            // already at/near the cap, bail loudly instead of burning
            // through and returning a wall of misleading NoData results.
            if (remaining < pending.Count * 2)
            {
                int minutes = resetInSeconds / 60;
                throw new Exception(
                    $"WCL rate limit nearly exhausted: {spent:F0}/{limit:F0} points used this hour, " +
                    $"resets in ~{minutes} min. Skipping this run rather than returning bad data - try again after the reset.");
            }
        }

        var rows = new List<CandidateRow>();
        var eligible = new Dictionary<string, object?>();

        foreach (var (name, candidateData) in pending)
        {
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

            var result = await client.GetCharacterAsync(name, _config.ServerSlug, _config.ServerRegion, options.Partition, options.ZoneId);

            // Prefer the addon-captured class (instant, doesn't depend on
            // the WCL query succeeding) over WCL's own classID-resolved
            // name. This also fixes display filtering: previously it only
            // checked WCL's className, which is null on any query failure
            // (e.g. rate limiting) even when we already knew the class.
            string? effectiveClassName = preKnownClass ?? result.ClassName;

            CandidateStatus status;

            if (result.RawError == "character not found (null)")
            {
                status = CandidateStatus.NotFound;
            }
            else if (result.RawError != null && result.RawError.StartsWith("no zoneRankings"))
            {
                status = CandidateStatus.NoData;
            }
            else if (!result.Found && result.RawError != null)
            {
                // A real failure (bad query, auth problem, HTTP error, etc.)
                // - surface it instead of silently lumping it in with NoData.
                status = CandidateStatus.Error;
            }
            else if (!result.BestParsePercent.HasValue || !result.AverageItemLevel.HasValue)
            {
                status = CandidateStatus.NoData;
            }
            else
            {
                bool meetsOverall = result.BestParsePercent >= options.MinBestParsePercent
                    && result.AverageItemLevel >= options.MinAverageItemLevel;

                bool meetsClass = options.AllowedClasses.Count == 0
                    || (effectiveClassName != null && options.AllowedClasses.Contains(effectiveClassName));

                bool meetsPerBoss = !options.UsePerBossParse
                    || (result.PerBoss.Count > 0 && result.PerBoss.All(b => b.RankPercent >= options.MinPerBossParsePercent));

                status = (meetsOverall && meetsClass && meetsPerBoss) ? CandidateStatus.Pass : CandidateStatus.Fail;
            }

            // Always surface the raw diagnostic text when present, regardless
            // of which bucket it landed in - this is what let us catch the
            // Scrotul regression instead of guessing at it.
            var row = new CandidateRow(
                name, status, result.BestParsePercent, result.AverageItemLevel,
                result.Spec, effectiveClassName, result.PerBoss, result.RawError);

            rows.Add(row);
            onRow?.Invoke(row);

            if (status == CandidateStatus.Pass)
            {
                eligible[name] = new Dictionary<string, object?>
                {
                    ["bestParsePercent"] = result.BestParsePercent,
                    ["medianParsePercent"] = result.MedianParsePercent,
                    ["averageItemLevel"] = result.AverageItemLevel,
                    ["spec"] = result.Spec,
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