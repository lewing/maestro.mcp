using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaestroTool.Core;

public class AzDoApiClient : IAzDoApiClient
{
    // Lazy init defers subprocess auth (az account get-access-token) to first API call,
    // preventing TypeInitializationException from crashing the server at startup.
    private static readonly Lazy<HttpClient> _lazyHttpClient = new(CreateHttpClient);
    private static HttpClient _httpClient => _lazyHttpClient.Value;
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
            // Use cmd /c on Windows because az is az.cmd (batch file)
            if (OperatingSystem.IsWindows())
            {
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = "/c az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798";
            }
            else
            {
                process.StartInfo.FileName = "az";
                process.StartInfo.Arguments = "account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798";
            }
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            // Read stdout async to avoid deadlock (ReadToEnd blocks until process exits)
            var outputTask = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync(); // drain stderr to prevent buffer deadlock

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(); } catch { }
                Console.Error.WriteLine("[maestro-mcp] AzDO auth: az CLI timed out");
                return null;
            }

            var output = outputTask.Result.Trim();
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

        var url = BuildCommitsUrl(org, project, repo, baseSha, headSha);
        
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
        catch (Exception)
        {
            Console.Error.WriteLine($"[maestro-mcp] AzDO commits API auth failed for {org}/{project}/{repo} — set AZDO_TOKEN for internal repos");
            return null;
        }
    }

    public async Task<IReadOnlyList<CommitInfo>?> GetCommitDetailsAsync(string org, string project, string repo,
        string baseSha, string headSha, CancellationToken ct = default)
    {
        if (!_shaPattern.IsMatch(baseSha) || !_shaPattern.IsMatch(headSha))
            return null;

        var url = BuildCommitsUrl(org, project, repo, baseSha, headSha);

        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                return null;

            var commits = new List<CommitInfo>();
            foreach (var c in value.EnumerateArray())
            {
                if (commits.Count >= 25) break;

                var sha = c.GetProperty("commitId").GetString() ?? "";
                var fullMessage = c.GetProperty("comment").GetString() ?? "";
                var message = fullMessage.Split('\n', 2)[0];
                var authorName = "unknown";
                var date = DateTimeOffset.MinValue;

                if (c.TryGetProperty("author", out var author))
                {
                    authorName = author.GetProperty("name").GetString() ?? "unknown";
                    var dateStr = author.GetProperty("date").GetString();
                    if (DateTimeOffset.TryParse(dateStr, out var d)) date = d;
                }

                commits.Add(new CommitInfo(sha.Length > 7 ? sha[..7] : sha, message, authorName, date));
            }

            return commits;
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"[maestro-mcp] AzDO commits API failed for {org}/{project}/{repo}");
            return null;
        }
    }

    private static string BuildCommitsUrl(string org, string project, string repo, string baseSha, string headSha) =>
        $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}/commits?" +
        $"searchCriteria.itemVersion.version={headSha}&searchCriteria.itemVersion.versionType=commit&" +
        $"searchCriteria.compareVersion.version={baseSha}&searchCriteria.compareVersion.versionType=commit&" +
        $"searchCriteria.$top=1000&api-version=7.1";
}
