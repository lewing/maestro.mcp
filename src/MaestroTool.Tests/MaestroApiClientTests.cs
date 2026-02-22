using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

/// <summary>
/// Integration tests for MaestroApiClient constructor / auth cascade.
/// These tests exercise the real PcsApiFactory (from NuGet) — they do NOT mock.
/// </summary>
public class MaestroApiClientTests
{
    private const string MaestroAppId = "54c17f3d-7325-4eca-9db7-f090bfc765a8";

    private static bool HasDarcAuthRecord()
    {
        var authRecordPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".darc",
            $".auth-record-{MaestroAppId}");
        return File.Exists(authRecordPath);
    }

    /// <summary>
    /// Regression test for Issue #8: PcsApiFactory.GetAnonymous() crashes with
    /// UriFormatException when called without a base URI.
    ///
    /// The auth cascade must always produce a working client — never throw
    /// UriFormatException. On machines with darc auth cached, the cascade
    /// resolves at EntraId; on machines without, it falls to Anonymous.
    /// Either way, it must not crash.
    /// </summary>
    [Fact]
    public void Constructor_NoBarToken_DoesNotThrowUriFormatException()
    {
        var savedToken = Environment.GetEnvironmentVariable("MAESTRO_BAR_TOKEN");
        Environment.SetEnvironmentVariable("MAESTRO_BAR_TOKEN", null);

        try
        {
            // Before Issue #8 fix: throws InvalidOperationException wrapping UriFormatException.
            // After fix: succeeds — resolves to EntraId (if darc cached) or Anonymous.
            var client = new MaestroApiClient(barToken: null);

            // Must resolve to a valid auth level — the specific level depends on
            // whether the test machine has cached darc credentials.
            Assert.True(
                client.AuthLevel == AuthLevel.EntraId || client.AuthLevel == AuthLevel.Anonymous,
                $"Expected EntraId or Anonymous, got {client.AuthLevel}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAESTRO_BAR_TOKEN", savedToken);
        }
    }

    /// <summary>
    /// Verifies that passing an empty string as barToken is treated the same as null
    /// (falls through to the next auth tier, not crash).
    /// </summary>
    [Fact]
    public void Constructor_EmptyBarToken_FallsBackWithoutCrashing()
    {
        var savedToken = Environment.GetEnvironmentVariable("MAESTRO_BAR_TOKEN");
        Environment.SetEnvironmentVariable("MAESTRO_BAR_TOKEN", null);

        try
        {
            var client = new MaestroApiClient(barToken: "");

            // Empty string should be treated as "no token" — cascade continues
            Assert.True(
                client.AuthLevel == AuthLevel.EntraId || client.AuthLevel == AuthLevel.Anonymous,
                $"Expected EntraId or Anonymous, got {client.AuthLevel}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAESTRO_BAR_TOKEN", savedToken);
        }
    }

    /// <summary>
    /// Verifies the AuthLevel enum has the three expected values.
    /// </summary>
    [Fact]
    public void AuthLevel_HasExpectedValues()
    {
        Assert.Equal(0, (int)AuthLevel.Pat);
        Assert.Equal(1, (int)AuthLevel.EntraId);
        Assert.Equal(2, (int)AuthLevel.Anonymous);
    }
}
