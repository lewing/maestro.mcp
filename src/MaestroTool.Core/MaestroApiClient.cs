using Microsoft.DotNet.ProductConstructionService.Client;
using Microsoft.DotNet.ProductConstructionService.Client.Models;

namespace MaestroTool.Core;

/// <summary>
/// Wraps the PCS NuGet client, adapting it to our <see cref="IMaestroApiClient"/> interface.
/// </summary>
public class MaestroApiClient : IMaestroApiClient
{
    private readonly IProductConstructionServiceApi _api;

    /// <summary>
    /// The authentication level that was resolved during client creation.
    /// </summary>
    public AuthLevel AuthLevel { get; }

    /// <summary>
    /// Auth cascade: BAR token → Entra ID (cached darc credentials) → anonymous.
    /// </summary>
    public MaestroApiClient(string? barToken = null)
    {
        var (api, authLevel) = CreateApi(barToken);
        _api = api;
        AuthLevel = authLevel;
    }

    private const string MaestroAppId = "54c17f3d-7325-4eca-9db7-f090bfc765a8";
    private const string DefaultBaseUri = "https://maestro.dot.net";

    private static (IProductConstructionServiceApi Api, AuthLevel Level) CreateApi(string? barToken)
    {
        // 1. Explicit BAR token from env var
        if (!string.IsNullOrEmpty(barToken))
        {
            try
            {
                Console.Error.WriteLine("[maestro-mcp] Auth: using MAESTRO_BAR_TOKEN");
                return (PcsApiFactory.GetAuthenticated(DefaultBaseUri, barToken, managedIdentityId: null, disableInteractiveAuth: true), AuthLevel.Pat);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[maestro-mcp] BAR token auth failed ({ex.GetType().Name}: {ex.Message}), falling back");
            }
        }

        // 2. Entra ID with MSAL cache (silent only — no browser popups).
        //    Only attempt this if darc has previously cached an auth record.
        //    disableInteractiveAuth: true prevents the credential from trying to open
        //    a browser, which would block/crash an MCP server process.
        var authRecordPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".darc",
            $".auth-record-{MaestroAppId}");

        if (File.Exists(authRecordPath))
        {
            try
            {
                var api = PcsApiFactory.GetAuthenticated(
                    DefaultBaseUri,
                    accessToken: null!,
                    managedIdentityId: null,
                    disableInteractiveAuth: true);

                Console.Error.WriteLine("[maestro-mcp] Auth: using Entra ID (cached darc credentials)");
                return (api, AuthLevel.EntraId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[maestro-mcp] Entra ID auth failed ({ex.GetType().Name}: {ex.Message}), falling back to anonymous");
            }
        }
        else
        {
            Console.Error.WriteLine("[maestro-mcp] No cached darc auth record found; run 'darc authenticate' for authenticated access");
        }

        // 3. Anonymous fallback — wrap in try/catch so the server always starts
        try
        {
            Console.Error.WriteLine("[maestro-mcp] Auth: anonymous (read-only access)");
            return (PcsApiFactory.GetAnonymous(DefaultBaseUri), AuthLevel.Anonymous);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] ⚠️ Anonymous API creation failed ({ex.GetType().Name}: {ex.Message})");
            Console.Error.WriteLine("[maestro-mcp] ⚠️ Server starting — tools will return errors until API is available");
            // Re-throw as last resort — we genuinely can't function without any API client
            throw new InvalidOperationException(
                $"Failed to initialize Maestro API client. Ensure .NET SDK is properly installed. Error: {ex.Message}", ex);
        }
    }

    public Task<List<Subscription>> ListSubscriptionsAsync(
        string? sourceRepository = null,
        string? targetRepository = null,
        int? channelId = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        return _api.Subscriptions.ListSubscriptionsAsync(
            sourceRepository: sourceRepository,
            targetRepository: targetRepository,
            channelId: channelId,
            enabled: enabled,
            cancellationToken: cancellationToken);
    }

    public Task<Subscription> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _api.Subscriptions.GetSubscriptionAsync(id, cancellationToken);
    }

    public Task<Build?> GetLatestBuildAsync(
        string repository,
        int? channelId = null,
        CancellationToken cancellationToken = default)
    {
        return _api.Builds.GetLatestAsync(
            repository: repository,
            channelId: channelId,
            loadCollections: true,
            cancellationToken: cancellationToken)!;
    }

    public Task<Build> GetBuildAsync(int id, CancellationToken cancellationToken = default)
    {
        return _api.Builds.GetBuildAsync(id, cancellationToken);
    }

    public async Task<List<Build>> ListBuildsAsync(
        string? repository = null,
        int? channelId = null,
        string? commit = null,
        string? buildNumber = null,
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        var page = await _api.Builds.ListBuildsPageAsync(
            repository: repository,
            channelId: channelId,
            commit: commit,
            buildNumber: buildNumber,
            azdoAccount: null,
            azdoBuildId: null,
            loadCollections: false,
            notBefore: null,
            notAfter: null,
            page: 1,
            perPage: count ?? 20,
            azdoProject: null,
            cancellationToken: cancellationToken);
        return page.Values.ToList();
    }

    public Task<Channel> GetChannelAsync(int id, CancellationToken cancellationToken = default)
    {
        return _api.Channels.GetChannelAsync(id, cancellationToken);
    }

    public Task<List<Channel>> ListChannelsAsync(CancellationToken cancellationToken = default)
    {
        return _api.Channels.ListChannelsAsync(cancellationToken: cancellationToken);
    }

    public Task<List<DefaultChannel>> ListDefaultChannelsAsync(
        string? repository = null,
        string? branch = null,
        int? channelId = null,
        CancellationToken cancellationToken = default)
    {
        return _api.DefaultChannels.ListAsync(
            repository: repository,
            branch: branch,
            channelId: channelId,
            cancellationToken: cancellationToken);
    }

    public Task<List<TrackedPullRequest>> GetTrackedPullRequestsAsync(CancellationToken cancellationToken = default)
    {
        return _api.PullRequest.GetTrackedPullRequestsAsync(cancellationToken: cancellationToken);
    }

    public Task<TrackedPullRequest> GetTrackedPullRequestBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        return _api.PullRequest.GetTrackedPullRequestBySubscriptionIdAsync(subscriptionId, cancellationToken: cancellationToken);
    }

    public Task<BackflowStatus> GetBackflowStatusAsync(int vmrBuildId, CancellationToken cancellationToken = default)
    {
        return _api.BackflowStatus.GetBackflowStatusAsync(vmrBuildId, cancellationToken: cancellationToken);
    }

    public async Task<List<SubscriptionHistoryItem>> GetSubscriptionHistoryAsync(Guid subscriptionId, int? page = null, int? perPage = null, CancellationToken cancellationToken = default)
    {
        var result = await _api.Subscriptions.GetSubscriptionHistoryPageAsync(subscriptionId, page, perPage, cancellationToken);
        return result.Values.ToList();
    }

    public async Task<Subscription> TriggerSubscriptionAsync(Guid subscriptionId, int buildId, bool force = false, CancellationToken cancellationToken = default)
    {
        return await _api.Subscriptions.TriggerSubscriptionAsync(buildId, force, subscriptionId, cancellationToken);
    }

    public Task TriggerDailyUpdateAsync(CancellationToken cancellationToken = default)
    {
        return _api.Subscriptions.TriggerDailyUpdateAsync(cancellationToken);
    }

    public Task<BuildGraph> GetBuildGraphAsync(int buildId, CancellationToken cancellationToken = default)
    {
        return _api.Builds.GetBuildGraphAsync(buildId, cancellationToken);
    }

    public Task<FlowGraph> GetFlowGraphAsync(int days, int channelId, bool includeArcade = true, bool includeBuildTimes = true, bool includeDisabledSubscriptions = false, List<string>? includedFrequencies = null, CancellationToken cancellationToken = default)
    {
        return _api.Channels.GetFlowGraphAsync(days, channelId, includeArcade, includeBuildTimes, includeDisabledSubscriptions, includedFrequencies ?? new List<string>(), cancellationToken);
    }
}
