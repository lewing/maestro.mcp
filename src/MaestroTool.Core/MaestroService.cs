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

    public MaestroService(IMaestroApiClient client, CacheService cache, IGitHubApiClient? gitHubClient = null)
    {
        _client = client;
        _cache = cache;
        _gitHubClient = gitHubClient;
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
                                }
                            }
                        }
                    }
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
                    CommitsBehind: commitsBehind
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

    private static bool IsVmrRepository(string? repoUrl) =>
        repoUrl != null && repoUrl.Contains("github.com/dotnet/dotnet", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitHubRepository(string? repoUrl) =>
        repoUrl != null && ParseGitHubUrl(repoUrl) != null;

    private static (string owner, string repo)? ParseGitHubUrl(string url)
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
    int? CommitsBehind = null
);

public record BuildFreshnessResult(
    string Channel,
    string AkaMsUrl,
    string? ResolvedUrl,
    DateTimeOffset? LastModified,
    bool IsAvailable,
    string? Error = null
);
