using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaestroTool.Core;

public class AzDoApiClient : IAzDoApiClient
{
    private static readonly HttpClient _httpClient = CreateHttpClient();
    private static readonly Regex _shaPattern = new(@"^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("maestro-mcp", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        
        // Auth cascade: 1. AZDO_TOKEN env var, 2. az CLI, 3. anonymous
        var token = GetAuthToken();
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static string? GetAuthToken()
    {
        // 1. Check AZDO_TOKEN env var
        var envToken = Environment.GetEnvironmentVariable("AZDO_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
        {
            Console.Error.WriteLine("[maestro-mcp] AzDO auth: using AZDO_TOKEN env var");
            return envToken;
        }

        // 2. Try az account get-access-token subprocess
        try
        {
            var process = new Process();
            process.StartInfo.FileName = "az";
            process.StartInfo.Arguments = "account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                Console.Error.WriteLine("[maestro-mcp] AzDO auth: az CLI timed out");
                return null;
            }
            
            if (process.ExitCode == 0 && output.Length > 0)
            {
                var doc = JsonDocument.Parse(output);
                var token = doc.RootElement.GetProperty("accessToken").GetString();
                if (!string.IsNullOrEmpty(token))
                {
                    Console.Error.WriteLine("[maestro-mcp] AzDO auth: using az CLI token");
                    return token;
                }
            }
        }
        catch
        {
            // az CLI not available or failed - fall through to anonymous
        }

        // 3. Fall back to anonymous
        Console.Error.WriteLine("[maestro-mcp] AzDO auth: anonymous");
        return null;
    }

    public async Task<int?> GetCommitCountAsync(string org, string project, string repo,
        string baseSha, string headSha, CancellationToken ct = default)
    {
        if (!_shaPattern.IsMatch(baseSha) || !_shaPattern.IsMatch(headSha))
            return null;

        var url = $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}/commits?" +
                  $"searchCriteria.itemVersion.version={headSha}&searchCriteria.itemVersion.versionType=commit&" +
                  $"searchCriteria.compareVersion.version={baseSha}&searchCriteria.compareVersion.versionType=commit&" +
                  $"searchCriteria.$top=1000&api-version=7.1";
        
        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[maestro-mcp] AzDO commits API auth failed for {org}/{project}/{repo} — set AZDO_TOKEN for internal repos");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value.GetArrayLength();
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] AzDO commits API auth failed for {org}/{project}/{repo} — set AZDO_TOKEN for internal repos");
            return null;
        }
    }
}
