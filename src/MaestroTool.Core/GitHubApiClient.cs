using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaestroTool.Core;

public class GitHubApiClient : IGitHubApiClient
{
    // Lazy init defers subprocess auth (gh auth token) to first API call,
    // preventing TypeInitializationException from crashing the server at startup.
    private static readonly Lazy<HttpClient> _lazyHttpClient = new(CreateHttpClient);
    private static HttpClient _httpClient => _lazyHttpClient.Value;
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
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            // Read stdout/stderr async to avoid deadlock (ReadToEnd blocks until process exits)
            var outputTask = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync(); // drain stderr to prevent buffer deadlock

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(); } catch { }
                Console.Error.WriteLine("[maestro-mcp] GitHub auth: gh CLI timed out");
                return null;
            }

            var token = outputTask.Result.Trim();
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
                TotalCommits: root.GetProperty("total_commits").GetInt32(),
                Commits: ParseCommits(root)
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] GitHub compare API exception: {ex.Message}");
            return null;
        }
    }

    public async Task<List<GitHubPullRequest>?> SearchMergedPullRequestsAsync(
        string owner, string repo, string sourceRepoFullName,
        DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        // GitHub search: find codeflow PRs by title pattern
        // Codeflow PRs are titled "[branch] Source code updates from org/repo"
        var titleSearch = Uri.EscapeDataString($"Source code updates from {sourceRepoFullName}");
        var query = $"repo:{owner}/{repo}+is:pr+is:merged+{titleSearch}+in:title";
        if (since.HasValue)
            query += $"+merged:>={since.Value:yyyy-MM-ddTHH:mm:ssZ}";

        var url = $"https://api.github.com/search/issues?q={query}&sort=updated&order=desc&per_page=10";

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[maestro-mcp] GitHub search API error: {response.StatusCode} for {owner}/{repo}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return new List<GitHubPullRequest>();

            var results = new List<GitHubPullRequest>();
            foreach (var item in items.EnumerateArray())
            {
                var number = item.GetProperty("number").GetInt32();
                var title = item.GetProperty("title").GetString() ?? "";
                // pull_request.merged_at is available in search results
                string? mergedAtStr = null;
                string? headBranch = null;
                string? mergeCommitSha = null;

                if (item.TryGetProperty("pull_request", out var prNode))
                {
                    if (prNode.TryGetProperty("merged_at", out var mergedAtProp) && mergedAtProp.ValueKind == JsonValueKind.String)
                        mergedAtStr = mergedAtProp.GetString();
                    if (prNode.TryGetProperty("merge_commit_sha", out var shaProp) && shaProp.ValueKind == JsonValueKind.String)
                        mergeCommitSha = shaProp.GetString();
                }

                // head branch isn't directly in search results; we searched by title
                headBranch = "";

                var mergedAt = DateTimeOffset.TryParse(mergedAtStr, out var parsed) ? parsed : DateTimeOffset.MinValue;
                if (mergedAt == DateTimeOffset.MinValue) continue; // skip if no merged_at

                results.Add(new GitHubPullRequest(number, title, headBranch, mergeCommitSha, mergedAt));
            }

            return results;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] GitHub search API exception: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<CommitInfo>? ParseCommits(JsonElement root)
    {
        if (!root.TryGetProperty("commits", out var commitsArray) || commitsArray.ValueKind != JsonValueKind.Array)
            return null;

        var commits = new List<CommitInfo>();
        foreach (var c in commitsArray.EnumerateArray())
        {
            if (commits.Count >= 25) break;

            var sha = c.GetProperty("sha").GetString() ?? "";
            var commit = c.GetProperty("commit");
            var fullMessage = commit.GetProperty("message").GetString() ?? "";
            var message = fullMessage.Split('\n', 2)[0]; // first line only
            var author = commit.GetProperty("author").GetProperty("name").GetString() ?? "unknown";
            var dateStr = commit.GetProperty("author").GetProperty("date").GetString();
            var date = DateTimeOffset.TryParse(dateStr, out var d) ? d : DateTimeOffset.MinValue;

            commits.Add(new CommitInfo(sha.Length > 7 ? sha[..7] : sha, message, author, date));
        }

        return commits;
    }
}
