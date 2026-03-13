namespace MaestroTool.Core;

public interface IAzDoApiClient
{
    Task<int?> GetCommitCountAsync(string org, string project, string repo,
        string baseSha, string headSha, CancellationToken ct = default);

    Task<IReadOnlyList<CommitInfo>?> GetCommitDetailsAsync(string org, string project, string repo,
        string baseSha, string headSha, CancellationToken ct = default);
}
