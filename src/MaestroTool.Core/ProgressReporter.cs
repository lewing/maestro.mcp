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
    public static int ItemStep(int total) => Math.Max(1, total / 10);
}
