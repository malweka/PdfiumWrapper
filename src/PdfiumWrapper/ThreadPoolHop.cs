using System.Runtime.CompilerServices;

namespace PdfiumWrapper;

/// <summary>
/// <c>await new ThreadPoolHop()</c> continues the async method on a thread-pool thread, unless it
/// already runs on one with no <see cref="SynchronizationContext"/> and the default scheduler.
/// Unlike <see cref="Task.Yield"/> it never posts to the caller's context, so a caller blocked in
/// <c>.Wait()</c> or <c>.Result</c> on a UI thread cannot deadlock the method, and page work does
/// not run on that UI thread.
/// </summary>
internal readonly struct ThreadPoolHop : ICriticalNotifyCompletion
{
    public ThreadPoolHop GetAwaiter() => this;

    public bool IsCompleted =>
        Thread.CurrentThread.IsThreadPoolThread
        && SynchronizationContext.Current == null
        && TaskScheduler.Current == TaskScheduler.Default;

    public void GetResult()
    {
    }

    public void OnCompleted(Action continuation)
        => ThreadPool.QueueUserWorkItem(static action => action(), continuation, preferLocal: false);

    public void UnsafeOnCompleted(Action continuation)
        => ThreadPool.UnsafeQueueUserWorkItem(static action => action(), continuation, preferLocal: false);
}
