using Microsoft.DotNet.ProductConstructionService.Client.Models;

namespace MaestroTool.Core;

/// <summary>
/// Abstraction over the PCS/Maestro API for testability.
/// </summary>
public interface IMaestroApiClient
{
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

    Task<List<Channel>> ListChannelsAsync(CancellationToken cancellationToken = default);

    Task<List<DefaultChannel>> ListDefaultChannelsAsync(
        string? repository = null,
        string? branch = null,
        int? channelId = null,
        CancellationToken cancellationToken = default);

    Task<Subscription> TriggerSubscriptionAsync(Guid subscriptionId, int buildId, CancellationToken cancellationToken = default);

    Task TriggerDailyUpdateAsync(CancellationToken cancellationToken = default);
}
