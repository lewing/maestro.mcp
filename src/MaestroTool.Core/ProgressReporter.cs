namespace MaestroTool.Core;

/// <summary>
/// Helpers for emitting coarse-grained <see cref="ProgressUpdate"/> events.
/// Aim is 5–10 updates over the lifetime of a long-running operation —
/// not per-item — to keep MCP traffic low.
/// </summary>
public static class ProgressReporter
{
    /// <summary>
    /// Compute the smallest step that yields no more than ~10 updates over
    /// <paramref name="total"/> items. Always at least 1.
    /// </summary>
    /// <remarks>
    /// Uses ceiling division so totals that aren't a clean multiple of 10
    /// still produce ≤10 updates (e.g. total=15 → step=2, not step=1).
    /// </remarks>
    public static int ItemStep(int total) => Math.Max(1, (total + 9) / 10);
}
