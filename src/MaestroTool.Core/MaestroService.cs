using System.Text.RegularExpressions;
using Microsoft.DotNet.ProductConstructionService.Client.Models;

namespace MaestroTool.Core;

/// <summary>
/// Business logic layer with caching for Maestro/BAR data.
/// </summary>
public class MaestroService
{
    private static readonly TimeSpan ShortTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MediumTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LongTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FreshnessTtl = TimeSpan.FromMinutes(10);

    private readonly IMaestroApiClient _client;
    private readonly CacheService _cache;
    private readonly IGitHubApiClient? _gitHubClient;
    private readonly IAzDoApiClient? _azDoClient;

    public MaestroService(IMaestroApiClient client, CacheService cache, IGitHubApiClient? gitHubClient = null, IAzDoApiClient? azDoClient = null)
    {
        _client = client;
        _cache = cache;
        _gitHubClient = gitHubClient;
        _azDoClient = azDoClient;
    }

    public async Task<List<Subscription>> GetSubscriptionsAsync(
        string? sourceRepository = null,
        string? targetRepository = null,
        int? channelId = null,
        string? targetBranch = null,
        bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var key = $"subs:{sourceRepository}:{targetRepository}:{channelId}:{targetBranch}";
        if (noCache) _cache.Invalidate(key);
        return await _cache.GetOrAddAsync(key,
            async () =>
            {
                var subs = await _client.ListSubscriptionsAsync(sourceRepository, targetRepository, channelId, enabled: true, cancellationToken);
                if (!string.IsNullOrEmpty(targetBranch))
                    subs = subs.Where(s => string.Equals(s.TargetBranch, targetBranch, StringComparison.OrdinalIgnoreCase)).ToList();
                return subs;
            },
            ShortTtl);
    }

    public Task<Subscription> GetSubscriptionAsync(Guid id, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var key = $"sub:{id}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetSubscriptionAsync(id, cancellationToken),
            ShortTtl);
    }

    public Task<Build?> GetLatestBuildAsync(
        string repository,
        int? channelId = null,
        bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var key = $"latest-build:{repository}:{channelId}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetLatestBuildAsync(repository, channelId, cancellationToken),
            ShortTtl);
    }

    public Task<Build> GetBuildAsync(int id, bool noCache = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var key = $"build:{id}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetBuildAsync(id, cancellationToken),
            LongTtl); // Builds are immutable
    }

    public Task<List<Channel>> GetChannelsAsync(bool noCache = false, CancellationToken cancellationToken = default)
    {
        if (noCache) _cache.Invalidate("channels");
        return _cache.GetOrAddAsync("channels",
            () => _client.ListChannelsAsync(cancellationToken),
            MediumTtl);
    }

    public Task<Channel> GetChannelAsync(int id, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var key = $"channel:{id}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetChannelAsync(id, cancellationToken),
            MediumTtl);
    }

    public Task<List<Build>> ListBuildsAsync(
        string? repository = null,
        int? channelId = null,
        string? commit = null,
        string? buildNumber = null,
        int? count = null,
        bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var key = $"builds:{repository}:{channelId}:{commit}:{buildNumber}:{count}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.ListBuildsAsync(repository, channelId, commit, buildNumber, count, cancellationToken),
            ShortTtl);
    }

    public async Task<Channel?> GetChannelByNameAsync(string name, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var channels = await GetChannelsAsync(noCache, cancellationToken);
        return channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public Task<List<DefaultChannel>> GetDefaultChannelsAsync(
        string? repository = null,
        string? branch = null,
        bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var key = $"default-channels:{repository}:{branch}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.ListDefaultChannelsAsync(repository, branch, cancellationToken: cancellationToken),
            MediumTtl);
    }

    /// <summary>
    /// For each subscription targeting the given repo, check if the last applied build
    /// matches the latest build on the channel. Returns subscription health diagnostics.
    /// </summary>
    public async Task<List<SubscriptionHealthResult>> GetSubscriptionHealthAsync(
        string targetRepository,
        bool noCache = false,
        bool includeCommitDetails = false,
        bool validate = false,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await GetSubscriptionsAsync(targetRepository: targetRepository, noCache: noCache, cancellationToken: cancellationToken);
        var results = new List<SubscriptionHealthResult>();

        foreach (var sub in subscriptions)
        {
            var channelId = sub.Channel?.Id;
            if (channelId == null) continue;

            try
            {
                var latestBuild = await GetLatestBuildAsync(sub.SourceRepository, channelId, noCache, cancellationToken);
                var lastApplied = sub.LastAppliedBuild;

                var isStale = latestBuild != null && lastApplied != null && latestBuild.Id != lastApplied.Id;
                var buildsBehind = 0;
                int? commitsBehind = null;
                IReadOnlyList<CommitInfo>? recentCommits = null;

                if (isStale && latestBuild != null && lastApplied != null)
                {
                    buildsBehind = latestBuild.Id - lastApplied.Id; // Approximate

                    // For GitHub-hosted source repos, use GitHub compare API for accurate commit distance
                    if (_gitHubClient != null && IsGitHubRepository(sub.SourceRepository))
                    {
                        var parsedRepo = ParseGitHubUrl(sub.SourceRepository);
                        if (parsedRepo.HasValue)
                        {
                            // Fetch full build objects if commit SHAs are missing
                            var lastAppliedCommit = lastApplied.Commit;
                            var latestBuildCommit = latestBuild.Commit;

                            if (string.IsNullOrEmpty(lastAppliedCommit) && lastApplied.Id > 0)
                            {
                                Console.Error.WriteLine($"[maestro-mcp] Fetching full build {lastApplied.Id} for commit SHA");
                                var fullLastApplied = await GetBuildAsync(lastApplied.Id, noCache, cancellationToken);
                                lastAppliedCommit = fullLastApplied?.Commit;
                            }

                            if (string.IsNullOrEmpty(latestBuildCommit) && latestBuild.Id > 0)
                            {
                                Console.Error.WriteLine($"[maestro-mcp] Fetching full build {latestBuild.Id} for commit SHA");
                                var fullLatestBuild = await GetBuildAsync(latestBuild.Id, noCache, cancellationToken);
                                latestBuildCommit = fullLatestBuild?.Commit;
                            }

                            if (!string.IsNullOrEmpty(lastAppliedCommit) && !string.IsNullOrEmpty(latestBuildCommit))
                            {
                                var (owner, repo) = parsedRepo.Value;
                                Console.Error.WriteLine($"[maestro-mcp] Comparing commits {lastAppliedCommit}...{latestBuildCommit} in {owner}/{repo}");
                                var compareResult = await _gitHubClient.CompareCommitsAsync(
                                    owner, repo, lastAppliedCommit, latestBuildCommit, cancellationToken);
                                
                                if (compareResult != null)
                                {
                                    commitsBehind = compareResult.AheadBy;
                                    if (includeCommitDetails)
                                        recentCommits = compareResult.Commits;
                                }
                            }
                        }
                    }
                    else if (_azDoClient != null && IsAzDoRepository(sub.SourceRepository))
                    {
                        var parsed = ParseAzDoUrl(sub.SourceRepository);
                        if (parsed.HasValue)
                        {
                            var lastAppliedCommit = lastApplied.Commit;
                            var latestBuildCommit = latestBuild.Commit;

                            if (string.IsNullOrEmpty(lastAppliedCommit) && lastApplied.Id > 0)
                            {
                                var fullLastApplied = await GetBuildAsync(lastApplied.Id, noCache, cancellationToken);
                                lastAppliedCommit = fullLastApplied?.Commit;
                            }

                            if (string.IsNullOrEmpty(latestBuildCommit) && latestBuild.Id > 0)
                            {
                                var fullLatestBuild = await GetBuildAsync(latestBuild.Id, noCache, cancellationToken);
                                latestBuildCommit = fullLatestBuild?.Commit;
                            }

                            if (!string.IsNullOrEmpty(lastAppliedCommit) && !string.IsNullOrEmpty(latestBuildCommit))
                            {
                                var (org, project, repo) = parsed.Value;
                                if (includeCommitDetails)
                                {
                                    recentCommits = await _azDoClient.GetCommitDetailsAsync(
                                        org, project, repo, lastAppliedCommit, latestBuildCommit, cancellationToken);
                                    commitsBehind = recentCommits?.Count;
                                }
                                else
                                {
                                    commitsBehind = await _azDoClient.GetCommitCountAsync(
                                        org, project, repo, lastAppliedCommit, latestBuildCommit, cancellationToken);
                                }
                            }
                        }
                    }
                }

                // Cross-validation: only for stale GitHub-hosted target repos when validate=true
                ValidationResult? validation = null;
                if (isStale && validate && _gitHubClient != null && IsGitHubRepository(sub.TargetRepository))
                {
                    validation = await CrossValidateSubscriptionAsync(sub, lastApplied, noCache, cancellationToken);
                }

                // Canary check: cheap heuristic for stuck bookkeeping (runs even without validate=true)
                string? canaryWarning = null;
                if (isStale)
                {
                    canaryWarning = await CheckCanaryWarningAsync(sub.Id, noCache, cancellationToken);
                }

                results.Add(new SubscriptionHealthResult(
                    SubscriptionId: sub.Id,
                    SourceRepository: sub.SourceRepository,
                    TargetRepository: sub.TargetRepository,
                    TargetBranch: sub.TargetBranch,
                    ChannelName: sub.Channel?.Name ?? "unknown",
                    IsStale: isStale,
                    BuildsBehind: buildsBehind,
                    LastAppliedBuildId: lastApplied?.Id,
                    LastAppliedDate: lastApplied?.DateProduced,
                    LatestBuildId: latestBuild?.Id,
                    LatestBuildDate: latestBuild?.DateProduced,
                    CommitsBehind: commitsBehind,
                    RecentCommits: recentCommits,
                    Validation: validation,
                    CanaryWarning: canaryWarning
                ));
            }
            catch (Exception ex)
            {
                results.Add(new SubscriptionHealthResult(
                    SubscriptionId: sub.Id,
                    SourceRepository: sub.SourceRepository,
                    TargetRepository: sub.TargetRepository,
                    TargetBranch: sub.TargetBranch,
                    ChannelName: sub.Channel?.Name ?? "unknown",
                    IsStale: false,
                    BuildsBehind: 0,
                    LastAppliedBuildId: sub.LastAppliedBuild?.Id,
                    LastAppliedDate: sub.LastAppliedBuild?.DateProduced,
                    LatestBuildId: null,
                    LatestBuildDate: null,
                    Error: ex.Message
                ));
            }
        }

        return results;
    }

    /// <summary>
    /// Check build freshness by resolving aka.ms URLs.
    /// </summary>
    public async Task<BuildFreshnessResult> GetBuildFreshnessAsync(
        string channel,
        bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        // SSRF mitigation: validate channel parameter (alphanumeric, dots, hyphens only)
        if (!Regex.IsMatch(channel, @"^[a-zA-Z0-9.\-]+$"))
            return new BuildFreshnessResult(channel, "", null, null, IsAvailable: false,
                Error: "Invalid channel name. Only alphanumeric characters, dots, and hyphens are allowed.");

        var key = $"freshness:{channel}";
        if (noCache) _cache.Invalidate(key);
        return await _cache.GetOrAddAsync(key, async () =>
        {
            var akaMsUrl = $"https://aka.ms/dotnet/{channel}/daily/productCommit-win-x64.txt";

            using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            try
            {
                var response = await httpClient.GetAsync(akaMsUrl, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.Redirect ||
                    response.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
                {
                    var redirectUrl = response.Headers.Location?.ToString();
                    if (!string.IsNullOrEmpty(redirectUrl))
                    {
                        // SSRF mitigation: validate redirect URL stays within expected Microsoft domains
                        if (Uri.TryCreate(redirectUrl, UriKind.Absolute, out var redirectUri))
                        {
                            var host = redirectUri.Host;
                            if (!host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) &&
                                !host.Contains("dotnetcli", StringComparison.OrdinalIgnoreCase) &&
                                !host.Equals("ci.dot.net", StringComparison.OrdinalIgnoreCase) &&
                                !host.EndsWith(".azureedge.net", StringComparison.OrdinalIgnoreCase))
                            {
                                return new BuildFreshnessResult(channel, akaMsUrl, redirectUrl, null, IsAvailable: false,
                                    Error: $"Redirect URL points to unexpected domain: {host}");
                            }
                        }
                        else
                        {
                            return new BuildFreshnessResult(channel, akaMsUrl, redirectUrl, null, IsAvailable: false,
                                Error: "Redirect URL is not a valid absolute URI.");
                        }

                        // Follow the redirect and check Last-Modified
                        using var httpClient2 = new HttpClient();
                        var headResponse = await httpClient2.SendAsync(
                            new HttpRequestMessage(HttpMethod.Head, redirectUrl), cancellationToken);

                        var lastModified = headResponse.Content.Headers.LastModified;
                        return new BuildFreshnessResult(
                            Channel: channel,
                            AkaMsUrl: akaMsUrl,
                            ResolvedUrl: redirectUrl,
                            LastModified: lastModified,
                            IsAvailable: true
                        );
                    }
                }

                return new BuildFreshnessResult(channel, akaMsUrl, null, null, IsAvailable: false);
            }
            catch (Exception ex)
            {
                return new BuildFreshnessResult(channel, akaMsUrl, null, null, IsAvailable: false, Error: ex.Message);
            }
        }, FreshnessTtl);
    }

    public Task<List<TrackedPullRequest>> GetTrackedPullRequestsAsync(int? channelId = null, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var key = $"tracked-prs:{channelId}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key, async () =>
        {
            var prs = await _client.GetTrackedPullRequestsAsync(cancellationToken);
            if (channelId.HasValue)
                prs = prs.Where(pr => pr.Channel?.Id == channelId.Value).ToList();
            return prs;
        }, ShortTtl);
    }

    public Task<TrackedPullRequest> GetTrackedPullRequestBySubscriptionIdAsync(string subscriptionId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var key = $"tracked-pr:{subscriptionId}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetTrackedPullRequestBySubscriptionIdAsync(subscriptionId, cancellationToken),
            ShortTtl);
    }

    public Task<BackflowStatus> GetBackflowStatusAsync(int vmrBuildId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var key = $"backflow-status:{vmrBuildId}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetBackflowStatusAsync(vmrBuildId, cancellationToken),
            ShortTtl);
    }

    public Task<List<SubscriptionHistoryItem>> GetSubscriptionHistoryAsync(Guid subscriptionId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var key = $"sub-history:{subscriptionId}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetSubscriptionHistoryAsync(subscriptionId, cancellationToken: cancellationToken),
            ShortTtl);
    }

    public async Task<Subscription> TriggerSubscriptionAsync(Guid subscriptionId, int buildId, bool force = false, CancellationToken cancellationToken = default)
    {
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] Trigger: TriggerSubscriptionAsync args=(subscriptionId={subscriptionId}, buildId={buildId}, force={force})");

        if (_client.AuthLevel == AuthLevel.Anonymous)
            throw new InvalidOperationException("Authentication required to trigger subscriptions. Run 'darc authenticate' or set MAESTRO_BAR_TOKEN.");

        var result = await _client.TriggerSubscriptionAsync(subscriptionId, buildId, force, cancellationToken);
        // Invalidate cached subscription data since it may have changed
        _cache.Invalidate($"sub:{subscriptionId}");
        _cache.InvalidatePrefix($"subs:");
        return result;
    }

    public async Task TriggerDailyUpdateAsync(CancellationToken cancellationToken = default)
    {
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] Trigger: TriggerDailyUpdateAsync");

        if (_client.AuthLevel == AuthLevel.Anonymous)
            throw new InvalidOperationException("Authentication required to trigger daily updates. Run 'darc authenticate' or set MAESTRO_BAR_TOKEN.");

        await _client.TriggerDailyUpdateAsync(cancellationToken);
        // Invalidate subscription-related caches since updates may have occurred
        _cache.InvalidatePrefix($"subs:");
    }

    public Task<BuildGraph> GetBuildGraphAsync(int buildId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buildId);
        var key = $"build-graph:{buildId}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetBuildGraphAsync(buildId, cancellationToken),
            LongTtl); // Build graphs are immutable like builds
    }

    public Task<FlowGraph> GetFlowGraphAsync(int days, int channelId, bool includeArcade = true, bool includeBuildTimes = true, bool includeDisabledSubscriptions = false, List<string>? includedFrequencies = null, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var key = $"flow-graph:{channelId}:{days}:{includeArcade}:{includeBuildTimes}:{includeDisabledSubscriptions}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetFlowGraphAsync(days, channelId, includeArcade, includeBuildTimes, includeDisabledSubscriptions, includedFrequencies, cancellationToken),
            ShortTtl);
    }

    public Task<List<CodeflowStatus>> GetCodeflowStatusesAsync(string repositoryUrl, string branch, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var key = $"codeflow-statuses:{repositoryUrl}:{branch}";
        if (noCache) _cache.Invalidate(key);
        return _cache.GetOrAddAsync(key,
            () => _client.GetCodeflowStatusesAsync(repositoryUrl, branch, cancellationToken),
            ShortTtl);
    }

    /// <summary>
    /// Cross-validate a stale subscription against GitHub ground truth.
    /// Checks commit reachability and searches for merged codeflow PRs.
    /// </summary>
    private async Task<ValidationResult?> CrossValidateSubscriptionAsync(
        Subscription sub, Build? lastApplied,
        bool noCache, CancellationToken cancellationToken)
    {
        if (_gitHubClient == null) return null;

        var targetParsed = ParseGitHubUrl(sub.TargetRepository);
        if (!targetParsed.HasValue) return null;
        var (targetOwner, targetRepo) = targetParsed.Value;

        var cacheKey = $"validation:{sub.Id}:{lastApplied?.Id}";
        if (noCache) _cache.Invalidate(cacheKey);

        return await _cache.GetOrAddAsync(cacheKey, async () =>
        {
            var commitReachable = true;
            var mergedPrCount = 0;
            List<string>? mergedPrUrls = null;
            var anomalyDetected = false;
            string? anomalyReason = null;

            // 1. Commit reachability check on the SOURCE repo
            if (lastApplied != null && !string.IsNullOrEmpty(lastApplied.Commit) && IsGitHubRepository(sub.SourceRepository))
            {
                var sourceParsed = ParseGitHubUrl(sub.SourceRepository);
                if (sourceParsed.HasValue)
                {
                    var (srcOwner, srcRepo) = sourceParsed.Value;
                    // Compare lastApplied.Commit against HEAD of target branch
                    // If 404 → commit not reachable (corrupted bookkeeping)
                    var compareResult = await _gitHubClient.CompareCommitsAsync(
                        srcOwner, srcRepo, lastApplied.Commit, "HEAD", cancellationToken);
                    commitReachable = compareResult != null;
                    if (!commitReachable)
                    {
                        anomalyDetected = true;
                        anomalyReason = $"LastAppliedBuild commit {lastApplied.Commit[..Math.Min(7, lastApplied.Commit.Length)]} not reachable in {sub.SourceRepository}";
                    }
                }
            }

            // 2. PR ground truth check: search for merged PRs in target repo
            //    that match codeflow branch patterns from the source repo
            if (lastApplied?.DateProduced != null)
            {
                // Extract source repo short name for branch pattern matching
                // e.g. "https://github.com/dotnet/emsdk" → "emsdk"
                var sourceParsed = ParseGitHubUrl(sub.SourceRepository);
                var branchPattern = sourceParsed.HasValue ? sourceParsed.Value.repo : null;

                if (branchPattern != null)
                {
                    Console.Error.WriteLine($"[maestro-mcp] Cross-validation: searching merged PRs in {targetOwner}/{targetRepo} with head:{branchPattern} since {lastApplied.DateProduced:u}");
                    var mergedPrs = await _gitHubClient.SearchMergedPullRequestsAsync(
                        targetOwner, targetRepo, branchPattern,
                        lastApplied.DateProduced, cancellationToken);

                    if (mergedPrs is { Count: > 0 })
                    {
                        mergedPrCount = mergedPrs.Count;
                        mergedPrUrls = mergedPrs
                            .Select(pr => $"https://github.com/{targetOwner}/{targetRepo}/pull/{pr.Number}")
                            .ToList();
                        anomalyDetected = true;
                        anomalyReason = (anomalyReason != null ? anomalyReason + "; " : "")
                            + $"{mergedPrCount} PR(s) merged in target repo since LastAppliedDate ({lastApplied.DateProduced:u}) — bookkeeping likely stuck";
                    }
                }
            }

            return new ValidationResult(commitReachable, mergedPrCount, mergedPrUrls, anomalyDetected, anomalyReason);
        }, MediumTtl);
    }

    /// <summary>
    /// Canary warning: cheaply check if a subscription has suspicious history
    /// (many consecutive failures with zero successes → possible stuck bookkeeping).
    /// </summary>
    private async Task<string?> CheckCanaryWarningAsync(Guid subscriptionId, bool noCache, CancellationToken cancellationToken)
    {
        try
        {
            var history = await GetSubscriptionHistoryAsync(subscriptionId, noCache, cancellationToken);
            if (history.Count < 10) return null;

            var hasAnySuccess = history.Any(h => h.Success);
            if (!hasAnySuccess)
            {
                return $"⚠️ Possible bookkeeping anomaly: {history.Count} consecutive failures with no recorded successes";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Canary check failed for {subscriptionId}: {ex.Message}");
        }

        return null;
    }

    private static bool IsVmrRepository(string? repoUrl) =>
        repoUrl != null && repoUrl.Contains("github.com/dotnet/dotnet", StringComparison.OrdinalIgnoreCase);

    internal static bool IsGitHubRepository(string? repoUrl) =>
        repoUrl != null && ParseGitHubUrl(repoUrl) != null;

    internal static bool IsAzDoRepository(string? repoUrl) =>
        repoUrl != null && ParseAzDoUrl(repoUrl) != null;

    internal static (string owner, string repo)? ParseGitHubUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return null;

            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length >= 2)
            {
                return (segments[0], segments[1]);
            }
        }
        catch
        {
            // Invalid URL
        }
        return null;
    }

    internal static (string org, string project, string repo)? ParseAzDoUrl(string url)
    {
        try
        {
            var uri = new Uri(url.TrimEnd('/').Split('?')[0]);
            
            // Modern: https://dev.azure.com/{org}/{project}/_git/{repo}
            if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            {
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                if (segments.Length >= 4 && segments[2].Equals("_git", StringComparison.OrdinalIgnoreCase))
                {
                    return (segments[0], segments[1], segments[3]);
                }
            }
            // Legacy: https://{org}.visualstudio.com/{project}/_git/{repo}
            else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            {
                var org = uri.Host.Substring(0, uri.Host.IndexOf('.'));
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                if (segments.Length >= 3 && segments[1].Equals("_git", StringComparison.OrdinalIgnoreCase))
                {
                    return (org, segments[0], segments[2]);
                }
            }
        }
        catch
        {
            // Invalid URL
        }
        return null;
    }
}

public record SubscriptionHealthResult(
    Guid SubscriptionId,
    string SourceRepository,
    string TargetRepository,
    string TargetBranch,
    string ChannelName,
    bool IsStale,
    int BuildsBehind,
    int? LastAppliedBuildId,
    DateTimeOffset? LastAppliedDate,
    int? LatestBuildId,
    DateTimeOffset? LatestBuildDate,
    string? Error = null,
    int? CommitsBehind = null,
    IReadOnlyList<CommitInfo>? RecentCommits = null,
    ValidationResult? Validation = null,
    string? CanaryWarning = null
);

public record ValidationResult(
    bool CommitReachable,
    int MergedPrsSinceLastApplied,
    List<string>? MergedPrUrls,
    bool BookkeepingAnomalyDetected,
    string? AnomalyReason
);

public record BuildFreshnessResult(
    string Channel,
    string AkaMsUrl,
    string? ResolvedUrl,
    DateTimeOffset? LastModified,
    bool IsAvailable,
    string? Error = null
);
