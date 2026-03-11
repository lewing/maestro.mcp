namespace MaestroTool.Core;

public interface IGitHubApiClient
{
    Task<GitHubCompareResult?> CompareCommitsAsync(string owner, string repo, string baseSha, string headSha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for merged PRs in a target repo matching a source repo name in the title.
    /// Uses the GitHub search API to find codeflow PRs merged after a given date.
    /// Codeflow PR titles follow the pattern "[branch] Source code updates from org/repo".
    /// </summary>
    Task<List<GitHubPullRequest>?> SearchMergedPullRequestsAsync(
        string owner, string repo, string sourceRepoFullName,
        DateTimeOffset? since = null, CancellationToken cancellationToken = default);
}

public record CommitInfo(string Sha, string Message, string Author, DateTimeOffset Date);

public record GitHubCompareResult(int AheadBy, int BehindBy, string Status, int TotalCommits, IReadOnlyList<CommitInfo>? Commits = null);

public record GitHubPullRequest(int Number, string Title, string HeadBranch, string? MergeCommitSha, DateTimeOffset MergedAt);
