namespace MaestroTool.Core;

public interface IGitHubApiClient
{
    Task<GitHubCompareResult?> CompareCommitsAsync(string owner, string repo, string baseSha, string headSha, CancellationToken cancellationToken = default);
}

public record GitHubCompareResult(int AheadBy, int BehindBy, string Status, int TotalCommits);
