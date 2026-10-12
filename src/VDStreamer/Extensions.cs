using System;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace VDStreamer;

internal static class Extensions
{
    private static readonly Logger Logger = LogManager.GetLogger("Task");

    /// <summary>Fire-and-forget with exception logging.</summary>
    public static void Forget(this Task task)
    {
        if (task == null)
        {
            return;
        }
        task.ContinueWith(t => Logger.Debug(t.Exception?.GetBaseException(), "Background task failed"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public static void CancelSafe(this CancellationTokenSource cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
