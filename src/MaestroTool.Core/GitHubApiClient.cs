using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaestroTool.Core;

public class GitHubApiClient : IGitHubApiClient
{
    private static readonly HttpClient _httpClient = CreateHttpClient();
    private static readonly Regex _shaPattern = new(@"^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("maestro-mcp", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        
        // Auth cascade: 1. GITHUB_TOKEN env var, 2. gh auth token, 3. anonymous
        var token = GetAuthToken();
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static string? GetAuthToken()
    {
        // 1. Check GITHUB_TOKEN env var
        var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
        {
            Console.Error.WriteLine("[maestro-mcp] GitHub auth: using GITHUB_TOKEN env var");
            return envToken;
        }

        // 2. Try gh auth token subprocess
        try
        {
            var process = new Process();
            process.StartInfo.FileName = "gh";
            process.StartInfo.Arguments = "auth token";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            var token = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                Console.Error.WriteLine("[maestro-mcp] GitHub auth: gh CLI timed out");
                return null;
            }
            
            if (process.ExitCode == 0 && token.Length > 0)
            {
                Console.Error.WriteLine("[maestro-mcp] GitHub auth: using gh CLI token");
                return token;
            }
        }
        catch
        {
            // gh CLI not available or failed - fall through to anonymous
        }

        // 3. Fall back to anonymous
        Console.Error.WriteLine("[maestro-mcp] GitHub auth: anonymous (60 req/hour)");
        return null;
    }

    public async Task<GitHubCompareResult?> CompareCommitsAsync(string owner, string repo, string baseSha, string headSha, CancellationToken cancellationToken = default)
    {
        if (!_shaPattern.IsMatch(baseSha) || !_shaPattern.IsMatch(headSha))
            return null;

        var url = $"https://api.github.com/repos/{owner}/{repo}/compare/{baseSha}...{headSha}";
        
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[maestro-mcp] GitHub compare API error: {response.StatusCode} for {owner}/{repo}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new GitHubCompareResult(
                AheadBy: root.GetProperty("ahead_by").GetInt32(),
                BehindBy: root.GetProperty("behind_by").GetInt32(),
                Status: root.GetProperty("status").GetString() ?? "unknown",
                TotalCommits: root.GetProperty("total_commits").GetInt32()
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] GitHub compare API exception: {ex.Message}");
            return null;
        }
    }
}
