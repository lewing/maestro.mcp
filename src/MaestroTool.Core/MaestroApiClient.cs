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
    /// Auth cascade: BAR token → Entra ID (cached darc credentials) → anonymous.
    /// </summary>
    public MaestroApiClient(string? barToken = null)
    {
        _api = CreateApi(barToken);
    }

    private const string MaestroAppId = "54c17f3d-7325-4eca-9db7-f090bfc765a8";

    private static IProductConstructionServiceApi CreateApi(string? barToken)
    {
        // 1. Explicit BAR token from env var
        if (!string.IsNullOrEmpty(barToken))
        {
            Console.Error.WriteLine("[maestro-mcp] Auth: using MAESTRO_BAR_TOKEN");
            return PcsApiFactory.GetAuthenticated(barToken, managedIdentityId: null, disableInteractiveAuth: true);
        }

        // 2. Entra ID via InteractiveBrowserCredential with MSAL cache.
        //    Only attempt this if darc has previously cached an auth record — otherwise
        //    the credential would try to open a browser, which blocks an MCP server.
        var authRecordPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".darc",
            $".auth-record-{MaestroAppId}");

        if (File.Exists(authRecordPath))
        {
            try
            {
                // disableInteractiveAuth: false → AppCredentialResolver uses InteractiveBrowserCredential
                // with the cached auth record + MSAL token cache, so no browser popup is needed.
                var api = PcsApiFactory.GetAuthenticated(
                    accessToken: null!,
                    managedIdentityId: null,
                    disableInteractiveAuth: false);

                Console.Error.WriteLine("[maestro-mcp] Auth: using Entra ID (cached darc credentials)");
                return api;
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

        // 3. Anonymous fallback
        Console.Error.WriteLine("[maestro-mcp] Auth: anonymous (read-only access)");
        return PcsApiFactory.GetAnonymous();
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
}
