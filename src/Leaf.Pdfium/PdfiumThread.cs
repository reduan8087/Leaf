using System.Diagnostics;
using Leaf.Pdfium.Native;

namespace Leaf.Pdfium;

/// <summary>
/// The one thread that is allowed to call pdfium ("None of the PDFium APIs are thread-safe").
/// Work is a priority queue: lower <c>priority</c> runs first; equal priorities run FIFO.
/// </summary>
public sealed unsafe class PdfiumThread
{
    private static readonly Lazy<PdfiumThread> s_instance = new(() => new PdfiumThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly PriorityQueue<Action, long> _queue = new();
    private uint _sequence;

    private PdfiumThread()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Leaf.Pdfium",
        };
        _thread.Start();
    }

    public static PdfiumThread Instance => s_instance.Value;

    public bool IsCurrent => Thread.CurrentThread == _thread;

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    [Conditional("DEBUG")]
    public static void AssertCurrent()
    {
        Debug.Assert(Instance.IsCurrent, "pdfium must only be used from PdfiumThread");
    }

    public Task<T> RunAsync<T>(Func<T> work, int priority = 0, CancellationToken cancellationToken = default)
    {
        if (IsCurrent)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, priority);
        return tcs.Task;
    }

    public Task RunAsync(Action work, int priority = 0, CancellationToken cancellationToken = default)
        => RunAsync(() => { work(); return true; }, priority, cancellationToken);

    /// <summary>Synchronous helper for tests and shutdown paths. Never call on the UI thread.</summary>
    public T Run<T>(Func<T> work, int priority = 0) => RunAsync(work, priority).GetAwaiter().GetResult();

    public void Run(Action work, int priority = 0) => RunAsync(work, priority).GetAwaiter().GetResult();

    private void Enqueue(Action action, int priority)
    {
        lock (_gate)
        {
            long key = ((long)priority << 32) | _sequence++;
            _queue.Enqueue(action, key);
            Monitor.Pulse(_gate);
        }
    }

    private void Loop()
    {
        var config = new FPDF_LIBRARY_CONFIG { Version = 2 };
        NativeMethods.FPDF_InitLibraryWithConfig(&config);

        while (true)
        {
            Action action;
            lock (_gate)
            {
                while (_queue.Count == 0)
                {
                    Monitor.Wait(_gate);
                }

                action = _queue.Dequeue();
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                // RunAsync wraps exceptions into the task; anything reaching here is a programming error.
                Debug.WriteLine($"[Leaf.Pdfium] unhandled work item exception: {ex}");
            }
        }
    }
}
