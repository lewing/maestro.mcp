namespace MaestroTool.Core;

public interface IGitHubApiClient
{
    Task<GitHubCompareResult?> CompareCommitsAsync(string owner, string repo, string baseSha, string headSha, CancellationToken cancellationToken = default);
}

public record CommitInfo(string Sha, string Message, string Author, DateTimeOffset Date);

public record GitHubCompareResult(int AheadBy, int BehindBy, string Status, int TotalCommits, IReadOnlyList<CommitInfo>? Commits = null);
