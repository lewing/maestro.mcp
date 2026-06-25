using Microsoft.DotNet.ProductConstructionService.Client.Models;

namespace MaestroTool.Core;

/// <summary>
/// Authentication level resolved during API client initialization.
/// </summary>
public enum AuthLevel
{
    Pat,
    EntraId,
    Anonymous
}

/// <summary>
/// Abstraction over the PCS/Maestro API for testability.
/// </summary>
public interface IMaestroApiClient
{
    /// <summary>
    /// The authentication level that was resolved during client creation.
    /// </summary>
    AuthLevel AuthLevel { get; }

    Task<List<Subscription>> ListSubscriptionsAsync(
        string? sourceRepository = null,
        string? targetRepository = null,
        int? channelId = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default);

    Task<Subscription> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Build?> GetLatestBuildAsync(
        string repository,
        int? channelId = null,
        CancellationToken cancellationToken = default);

    Task<Build> GetBuildAsync(int id, CancellationToken cancellationToken = default);

    Task<List<Build>> ListBuildsAsync(
        string? repository = null,
        int? channelId = null,
        string? commit = null,
        string? buildNumber = null,
        int? count = null,
        CancellationToken cancellationToken = default);

    Task<Channel> GetChannelAsync(int id, CancellationToken cancellationToken = default);

    Task<List<Channel>> ListChannelsAsync(CancellationToken cancellationToken = default, string? classification = null);

    Task<List<DefaultChannel>> ListDefaultChannelsAsync(
        string? repository = null,
        string? branch = null,
        int? channelId = null,
        CancellationToken cancellationToken = default);

    Task<List<TrackedPullRequest>> GetTrackedPullRequestsAsync(CancellationToken cancellationToken = default);

    Task<TrackedPullRequest> GetTrackedPullRequestBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default);

    Task<BackflowStatus> GetBackflowStatusAsync(int vmrBuildId, CancellationToken cancellationToken = default);

    Task<List<SubscriptionHistoryItem>> GetSubscriptionHistoryAsync(Guid subscriptionId, int? page = null, int? perPage = null, CancellationToken cancellationToken = default);

    Task<Subscription> TriggerSubscriptionAsync(Guid subscriptionId, int buildId, bool force = false, CancellationToken cancellationToken = default);

    Task TriggerDailyUpdateAsync(CancellationToken cancellationToken = default);

    Task<BuildGraph> GetBuildGraphAsync(int buildId, CancellationToken cancellationToken = default);

    Task<FlowGraph> GetFlowGraphAsync(int days, int channelId, bool includeArcade = true, bool includeBuildTimes = false, bool includeDisabledSubscriptions = false, List<string>? includedFrequencies = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get codeflow statuses for a repository and branch.
    /// </summary>
    Task<List<CodeflowStatus>> GetCodeflowStatusesAsync(string repositoryUrl, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// List subscription trigger outcomes.
    /// </summary>
    Task<List<SubscriptionTriggerOutcome>> ListSubscriptionOutcomesAsync(
        int limit,
        DateTimeOffset? after = null,
        DateTimeOffset? before = null,
        int? buildId = null,
        string? operationId = null,
        string? search = null,
        string? subscriptionId = null,
        string? subscriptionOutcomeType = null,
        CancellationToken cancellationToken = default);
}
