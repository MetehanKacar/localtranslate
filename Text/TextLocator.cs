using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace LocalTranslate.Text;

/// <summary>
/// Runs all UI Automation / OCR work on one dedicated MTA thread. UIA calls are
/// cross-process and can block for a while; doing them on the WPF thread would
/// freeze the overlay (and Microsoft advises against UIA clients calling from a
/// thread that owns windows).
/// </summary>
internal sealed class TextLocator : IDisposable
{
    readonly BlockingCollection<Action> queue = new();
    UiaReader? uia;

    public OcrReader Ocr { get; } = new();

    public TextLocator()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "LocalTranslate.TextLocator" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    void Run()
    {
        try { uia = new UiaReader(); } catch { uia = null; }
        foreach (var job in queue.GetConsumingEnumerable()) job();
    }

    public Task<TextHit?> SentenceAtAsync(Native.POINT p, bool allowOcr) => Enqueue(() =>
    {
        var (outcome, hit) = uia?.SentenceAt(p) ?? (UiaReader.Outcome.Unsupported, null);
        if (outcome == UiaReader.Outcome.Found) return hit;
        if (outcome == UiaReader.Outcome.OwnWindow || !allowOcr) return null;
        // No text there according to the app (an image, a canvas, an app that hides
        // its text): read the pixels. No sync context on this thread: safe to block.
        return Ocr.SentenceAtAsync(p).GetAwaiter().GetResult();
    });

    public Task<TextHit?> SelectionAsync(Native.POINT p) => Enqueue(() => uia?.Selection(p));

    Task<T> Enqueue<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            queue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
        }
        catch (InvalidOperationException) { tcs.SetCanceled(); } // disposed
        return tcs.Task;
    }

    public void Dispose() => queue.CompleteAdding();
}
