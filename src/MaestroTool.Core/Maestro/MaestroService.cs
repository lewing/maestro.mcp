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
    private readonly SemaphoreSlim _concurrencySemaphore = new(5, 5);

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

    public async Task<List<Channel>> GetChannelsAsync(
        bool noCache = false,
        CancellationToken cancellationToken = default,
        string? classification = null,
        string? filter = null)
    {
        var normalizedClassification = string.IsNullOrWhiteSpace(classification) ? null : classification.Trim();
        var key = normalizedClassification is null ? "channels" : $"channels:{normalizedClassification}";
        if (noCache) _cache.Invalidate(key);

        var channels = await _cache.GetOrAddAsync(key,
            () => _client.ListChannelsAsync(cancellationToken, normalizedClassification),
            MediumTtl);

        if (string.IsNullOrWhiteSpace(filter))
            return channels;

        var normalizedFilter = filter.Trim();
        return channels
            .Where(c => c.Name.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
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
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await GetSubscriptionsAsync(targetRepository: targetRepository, noCache: noCache, cancellationToken: cancellationToken);

        // Filter out subscriptions with no channel
        var validSubscriptions = subscriptions.Where(s => s.Channel?.Id != null).ToList();
        
        var total = validSubscriptions.Count;
        var step = ProgressReporter.ItemStep(total);  // ~10 updates total
        var completed = 0;
        
        progress?.Report(new ProgressUpdate(0, total, $"Checking {total} subscription(s)..."));

        // Parallelize subscription health checks with concurrency limit
        var tasks = validSubscriptions.Select(async sub =>
        {
            var result = await CheckSubscriptionHealthAsync(sub, noCache, includeCommitDetails, validate, cancellationToken);
            
            // Thread-safe progress tracking - emit at step intervals and final completion
            var done = System.Threading.Interlocked.Increment(ref completed);
            if (done == total || done % step == 0)
            {
                progress?.Report(new ProgressUpdate(done, total, $"Checked {done} of {total} subscriptions"));
            }
            
            return result;
        });
        
        var results = await Task.WhenAll(tasks);

        return results.ToList();
    }
    
    private static string FormatRepoName(string? repository)
    {
        if (string.IsNullOrEmpty(repository)) return "<unknown>";
        
        try
        {
            if (Uri.TryCreate(repository, UriKind.Absolute, out var uri))
            {
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                return segments.Length >= 2 ? $"{segments[^2]}/{segments[^1]}" : uri.AbsolutePath.Trim('/');
            }
        }
        catch
        {
            // Best-effort formatting; progress is cosmetic and must never fail the health check
        }
        
        return repository;
    }

    private async Task<SubscriptionHealthResult> CheckSubscriptionHealthAsync(
        Subscription sub,
        bool noCache,
        bool includeCommitDetails,
        bool validate,
        CancellationToken cancellationToken)
    {
        var channelId = sub.Channel!.Id; // Already validated in GetSubscriptionHealthAsync

        await _concurrencySemaphore.WaitAsync(cancellationToken);
        try
        {
            var latestBuild = await GetLatestBuildAsync(sub.SourceRepository, channelId, noCache, cancellationToken);
            var lastApplied = sub.LastAppliedBuild;

            var isStale = latestBuild != null && lastApplied != null && latestBuild.Id != lastApplied.Id;
            var buildsBehind = 0;
            int? commitsBehind = null;
            IReadOnlyList<CommitInfo>? recentCommits = null;
            string? latestOutcomeType = null;
            string? latestOutcomeMessage = null;

            if (isStale && latestBuild != null && lastApplied != null)
            {
                buildsBehind = latestBuild.Id - lastApplied.Id; // Approximate

                // Fetch latest outcome for stale subscriptions
                var outcomes = await GetSubscriptionOutcomesAsync(
                    subscriptionId: sub.Id,
                    count: 1,
                    noCache: noCache,
                    cancellationToken: cancellationToken);
                var latestOutcome = outcomes.FirstOrDefault();
                if (latestOutcome != null)
                {
                    latestOutcomeType = latestOutcome.Type.ToString();
                    latestOutcomeMessage = latestOutcome.Message;
                }

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

            // Oscillation detection: check for stuck state cycling (runs even without validate=true)
            OscillationResult? oscillation = null;
            string? vmrConsumedCommit = null;
            DateTimeOffset? vmrConsumedDate = null;
            TrackedPrDiagnosis? trackedPrDiagnosis = null;
            if (isStale)
            {
                oscillation = await DetectStateOscillationAsync(sub.Id, noCache, cancellationToken);

                // Tracked PR diagnosis: cross-reference with GitHub to determine why it's stuck
                trackedPrDiagnosis = await DiagnoseTrackedPrAsync(sub.Id, noCache, cancellationToken);

                // For VMR-targeting subscriptions, look up actual consumed commit
                if (IsVmrRepository(sub.TargetRepository))
                {
                    var manifestEntry = await GetVmrConsumedCommitAsync(sub.SourceRepository, sub.TargetBranch, noCache, cancellationToken);
                    if (manifestEntry != null)
                    {
                        vmrConsumedCommit = manifestEntry.CommitSha;
                        // Try to resolve date from barId if available
                        if (manifestEntry.BarId.HasValue)
                        {
                            try
                            {
                                var vmrBuild = await GetBuildAsync(manifestEntry.BarId.Value, noCache, cancellationToken);
                                vmrConsumedDate = vmrBuild?.DateProduced;
                            }
                            catch { /* non-critical */ }
                        }
                    }
                }
            }

            return new SubscriptionHealthResult(
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
                Oscillation: oscillation,
                TrackedPr: trackedPrDiagnosis,
                VmrConsumedCommit: vmrConsumedCommit,
                VmrConsumedDate: vmrConsumedDate,
                LatestOutcomeType: latestOutcomeType,
                LatestOutcomeMessage: latestOutcomeMessage
            );
        }
        catch (Exception ex)
        {
            return new SubscriptionHealthResult(
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
                Error: ex.Message,
                LatestOutcomeType: null,
                LatestOutcomeMessage: null
            );
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
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

    public Task<FlowGraph> GetFlowGraphAsync(int days, int channelId, bool includeArcade = true, bool includeBuildTimes = false, bool includeDisabledSubscriptions = false, List<string>? includedFrequencies = null, bool noCache = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);
        if (days > 30)
            throw new ArgumentOutOfRangeException(nameof(days), days, "Flow graph days must be between 1 and 30.");

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
    /// Get subscription trigger outcomes.
    /// </summary>
    public async Task<List<SubscriptionTriggerOutcome>> GetSubscriptionOutcomesAsync(
        Guid? subscriptionId = null,
        int? buildId = null,
        DateTimeOffset? after = null,
        DateTimeOffset? before = null,
        string? outcomeType = null,
        int? count = null,
        bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        // Defensive clamping: if count is null or non-positive, default to 20; cap at 100
        var limit = count is > 0 ? count.Value : 20;
        if (limit > 100)
            limit = 100;

        var key = $"sub-outcomes:{subscriptionId}:{buildId}:{after?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}:{before?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}:{outcomeType}:{limit}";
        if (noCache) _cache.Invalidate(key);
        return await _cache.GetOrAddAsync(key,
            async () =>
            {
                try
                {
                    return await _client.ListSubscriptionOutcomesAsync(
                        limit: limit,
                        after: after,
                        before: before,
                        buildId: buildId,
                        subscriptionId: subscriptionId?.ToString(),
                        subscriptionOutcomeType: outcomeType,
                        cancellationToken: cancellationToken);
                }
                catch (Microsoft.DotNet.ProductConstructionService.Client.RestApiException ex) when (ex.Response.Status == 404)
                {
                    // Subscriptions with zero trigger outcomes return 404 - this is expected
                    return new List<SubscriptionTriggerOutcome>();
                }
            },
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
            //    matching codeflow title pattern "Source code updates from org/repo"
            if (lastApplied?.DateProduced != null)
            {
                // Extract source repo full name for title matching
                // e.g. "https://github.com/dotnet/emsdk" → "dotnet/emsdk"
                var sourceParsed = ParseGitHubUrl(sub.SourceRepository);
                var sourceFullName = sourceParsed.HasValue ? $"{sourceParsed.Value.owner}/{sourceParsed.Value.repo}" : null;

                if (sourceFullName != null)
                {
                    Console.Error.WriteLine($"[maestro-mcp] Cross-validation: searching merged PRs in {targetOwner}/{targetRepo} with title matching \"{sourceFullName}\" since {lastApplied.DateProduced:u}");
                    var mergedPrs = await _gitHubClient.SearchMergedPullRequestsAsync(
                        targetOwner, targetRepo, sourceFullName,
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
    /// Detect stuck subscription by checking for state oscillation pattern
    /// (alternating ApplyingUpdates ↔ MergingPullRequest) in history.
    /// This is the primary signal for the arcade-services#6090 bug where
    /// merged PR state is never cleared from Redis.
    /// </summary>
    private async Task<OscillationResult?> DetectStateOscillationAsync(Guid subscriptionId, bool noCache, CancellationToken cancellationToken)
    {
        try
        {
            var history = await GetSubscriptionHistoryAsync(subscriptionId, noCache, cancellationToken);
            if (history.Count < 6) return null; // Need at least 6 entries for 3 oscillations

            // Get recent actions in chronological order
            var recentActions = history
                .OrderByDescending(h => h.Timestamp)
                .Take(50)
                .Select(h => (Action: h.Action ?? "", Timestamp: h.Timestamp))
                .Where(h => !string.IsNullOrEmpty(h.Action))
                .ToList();

            if (recentActions.Count < 6) return null;

            // Detect alternating pattern between exactly two states
            var distinctStates = recentActions.Select(a => a.Action).Distinct().ToList();
            if (distinctStates.Count != 2) return null;

            var state1 = distinctStates[0];
            var state2 = distinctStates[1];

            // Count oscillations: an oscillation is A→B→A
            int oscillationCount = 0;
            for (int i = 0; i < recentActions.Count - 2; i++)
            {
                if (recentActions[i].Action == recentActions[i + 2].Action &&
                    recentActions[i].Action != recentActions[i + 1].Action)
                {
                    oscillationCount++;
                }
                else
                {
                    break; // Stop counting at first break in pattern
                }
            }

            if (oscillationCount < 3) return null;

            return new OscillationResult(
                OscillationCount: oscillationCount,
                State1: state1,
                State2: state2,
                FirstSeen: recentActions.LastOrDefault().Timestamp,
                LastSeen: recentActions.FirstOrDefault().Timestamp
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Oscillation check failed for {subscriptionId}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// For stale subscriptions, check the tracked PR state to diagnose WHY it's stuck.
    /// Cross-references Maestro's tracked PR with GitHub to distinguish:
    /// - Stuck (PR merged but state not cleared) → arcade-services#6090
    /// - Blocked (PR open but CI failing)
    /// - Missing (no tracked PR at all)
    /// - Active (PR open and healthy — may be in progress)
    /// </summary>
    private async Task<TrackedPrDiagnosis?> DiagnoseTrackedPrAsync(Guid subscriptionId, bool noCache, CancellationToken cancellationToken)
    {
        try
        {
            var trackedPr = await GetTrackedPullRequestBySubscriptionIdAsync(subscriptionId.ToString(), noCache, cancellationToken);

            if (trackedPr == null || string.IsNullOrEmpty(trackedPr.Url))
            {
                return new TrackedPrDiagnosis(TrackedPrState.Missing, null, null);
            }

            string prUrl = trackedPr.Url;

            // Try to check the PR's actual state on GitHub
            if (_gitHubClient != null && TryParseGitHubPrUrl(prUrl, out var owner, out var repo, out var prNumber))
            {
                try
                {
                    var prState = await _gitHubClient.GetPullRequestStateAsync(owner, repo, prNumber, cancellationToken);
                    if (prState != null)
                    {
                        if (prState.Merged)
                        {
                            return new TrackedPrDiagnosis(TrackedPrState.MergedButNotCleared, prUrl, "PR merged but subscription state not cleared — arcade-services#6090");
                        }
                        else if (prState.Closed)
                        {
                            return new TrackedPrDiagnosis(TrackedPrState.ClosedButNotCleared, prUrl, "PR closed but subscription state not cleared");
                        }
                        else if (prState.ChecksFailing)
                        {
                            return new TrackedPrDiagnosis(TrackedPrState.BlockedByCI, prUrl, "PR open but CI checks are failing");
                        }
                        else
                        {
                            return new TrackedPrDiagnosis(TrackedPrState.Active, prUrl, null);
                        }
                    }
                }
                catch
                {
                    // If GitHub check fails, still report the PR URL
                }
            }

            // Fallback: we know a PR exists but can't check GitHub
            return new TrackedPrDiagnosis(TrackedPrState.Unknown, prUrl, null);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseGitHubPrUrl(string url, out string owner, out string repo, out int prNumber)
    {
        owner = repo = "";
        prNumber = 0;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "github.com")
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length >= 4 && segments[2] == "pull" && int.TryParse(segments[3], out prNumber))
        {
            owner = segments[0];
            repo = segments[1];
            return true;
        }
        return false;
    }

    /// <summary>
    /// For subscriptions targeting dotnet/dotnet, read source-manifest.json to find
    /// the commit SHA that the VMR actually consumed from the source repo.
    /// This is the ground truth for what code is in the VMR.
    /// </summary>
    private async Task<SourceManifestEntry?> GetVmrConsumedCommitAsync(string sourceRepository, string targetBranch, bool noCache, CancellationToken cancellationToken)
    {
        if (_gitHubClient == null) return null;

        var cacheKey = $"vmr-manifest:{sourceRepository}:{targetBranch}";
        if (noCache) _cache.Invalidate(cacheKey);

        return await _cache.GetOrAddAsync(cacheKey, async () =>
        {
            try
            {
                var content = await _gitHubClient.GetFileContentsAsync(
                    "dotnet", "dotnet", "src/source-manifest.json", targetBranch, cancellationToken);

                if (string.IsNullOrEmpty(content)) return null;

                var doc = System.Text.Json.JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("submodules", out var submodules) ||
                    submodules.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return null;

                foreach (var entry in submodules.EnumerateArray())
                {
                    var remoteUri = entry.TryGetProperty("remoteUri", out var uriProp) ? uriProp.GetString() : null;
                    if (remoteUri == null) continue;

                    // Normalize comparison: strip trailing slashes and .git suffix
                    var normalizedRemote = NormalizeRepoUrl(remoteUri);
                    var normalizedSource = NormalizeRepoUrl(sourceRepository);

                    if (string.Equals(normalizedRemote, normalizedSource, StringComparison.OrdinalIgnoreCase))
                    {
                        var commitSha = entry.TryGetProperty("commitSha", out var shaProp) ? shaProp.GetString() : null;
                        var path = entry.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : null;
                        int? barId = entry.TryGetProperty("barId", out var barProp) && barProp.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? barProp.GetInt32() : null;

                        if (commitSha != null)
                            return new SourceManifestEntry(commitSha, path ?? "", barId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[maestro-mcp] VMR manifest lookup failed: {ex.Message}");
            }

            return null;
        }, MediumTtl);
    }

    private static bool IsVmrRepository(string? repoUrl) =>
        repoUrl != null && repoUrl.Contains("github.com/dotnet/dotnet", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRepoUrl(string url)
    {
        var result = url.TrimEnd('/');
        if (result.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            result = result[..^4];
        return result;
    }

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
    OscillationResult? Oscillation = null,
    TrackedPrDiagnosis? TrackedPr = null,
    string? VmrConsumedCommit = null,
    DateTimeOffset? VmrConsumedDate = null,
    string? LatestOutcomeType = null,
    string? LatestOutcomeMessage = null
);

public record OscillationResult(
    int OscillationCount,
    string State1,
    string State2,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen
);

public enum TrackedPrState
{
    Missing,             // No tracked PR exists
    MergedButNotCleared, // PR merged but subscription still cycling (the #6090 bug)
    ClosedButNotCleared, // PR closed but subscription still cycling
    BlockedByCI,         // PR open but checks failing
    Active,              // PR open and healthy — might be in progress
    Unknown              // PR exists but couldn't check GitHub state
}

public record TrackedPrDiagnosis(
    TrackedPrState State,
    string? PrUrl,
    string? Reason
);

public record SourceManifestEntry(
    string CommitSha,
    string Path,
    int? BarId
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
