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

    public MaestroService(IMaestroApiClient client, CacheService cache)
    {
        _client = client;
        _cache = cache;
    }

    public Task<List<Subscription>> GetSubscriptionsAsync(
        string? sourceRepository = null,
        string? targetRepository = null,
        int? channelId = null,
        CancellationToken cancellationToken = default)
    {
        var key = $"subs:{sourceRepository}:{targetRepository}:{channelId}";
        return _cache.GetOrAddAsync(key,
            () => _client.ListSubscriptionsAsync(sourceRepository, targetRepository, channelId, enabled: true, cancellationToken),
            ShortTtl);
    }

    public Task<Subscription> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var key = $"sub:{id}";
        return _cache.GetOrAddAsync(key,
            () => _client.GetSubscriptionAsync(id, cancellationToken),
            ShortTtl);
    }

    public Task<Build?> GetLatestBuildAsync(
        string repository,
        int? channelId = null,
        CancellationToken cancellationToken = default)
    {
        var key = $"latest-build:{repository}:{channelId}";
        return _cache.GetOrAddAsync(key,
            () => _client.GetLatestBuildAsync(repository, channelId, cancellationToken),
            ShortTtl);
    }

    public Task<Build> GetBuildAsync(int id, CancellationToken cancellationToken = default)
    {
        var key = $"build:{id}";
        return _cache.GetOrAddAsync(key,
            () => _client.GetBuildAsync(id, cancellationToken),
            LongTtl); // Builds are immutable
    }

    public Task<List<Channel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        return _cache.GetOrAddAsync("channels",
            () => _client.ListChannelsAsync(cancellationToken),
            MediumTtl);
    }

    public async Task<Channel?> GetChannelByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var channels = await GetChannelsAsync(cancellationToken);
        return channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public Task<List<DefaultChannel>> GetDefaultChannelsAsync(
        string? repository = null,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        var key = $"default-channels:{repository}:{branch}";
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
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await GetSubscriptionsAsync(targetRepository: targetRepository, cancellationToken: cancellationToken);
        var results = new List<SubscriptionHealthResult>();

        foreach (var sub in subscriptions)
        {
            var channelId = sub.Channel?.Id;
            if (channelId == null) continue;

            var latestBuild = await GetLatestBuildAsync(sub.SourceRepository, channelId, cancellationToken);
            var lastApplied = sub.LastAppliedBuild;

            var isStale = latestBuild != null && lastApplied != null && latestBuild.Id != lastApplied.Id;
            var buildsBehind = 0;

            if (isStale && latestBuild != null && lastApplied != null)
            {
                buildsBehind = latestBuild.Id - lastApplied.Id; // Approximate
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
                LatestBuildDate: latestBuild?.DateProduced
            ));
        }

        return results;
    }

    /// <summary>
    /// Check build freshness by resolving aka.ms URLs.
    /// </summary>
    public async Task<BuildFreshnessResult> GetBuildFreshnessAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        var key = $"freshness:{channel}";
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
    DateTimeOffset? LatestBuildDate
);

public record BuildFreshnessResult(
    string Channel,
    string AkaMsUrl,
    string? ResolvedUrl,
    DateTimeOffset? LastModified,
    bool IsAvailable,
    string? Error = null
);
