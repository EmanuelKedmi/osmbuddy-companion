using System.Threading;

namespace MarketNotes.Companion;

/// <summary>
/// One companion window. A later Play click signals that window.
/// The ngm argument stays in the process that received it.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\MarketNotes.Companion.Instance";
    private const string EventName = @"Local\MarketNotes.Companion.NgmActivate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _event;
    private static volatile bool _listen;

    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(false, MutexName);
        try
        {
            if (_mutex.WaitOne(0))
            {
                _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public static void SignalActivate()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(EventName);
                handle.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }

    public static void Listen(Action activate)
    {
        var handle = _event;
        if (handle == null) return;
        _listen = true;
        var thread = new Thread(() =>
        {
            while (_listen)
            {
                if (!handle.WaitOne(500)) continue;
                if (_listen) activate();
            }
        })
        {
            IsBackground = true,
            Name = "NgmActivate",
        };
        thread.Start();
    }

    public static void Stop()
    {
        _listen = false;
        try
        {
            _event?.Set();
        }
        catch
        {
            // The event is already closed.
        }

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // The mutex was abandoned or never acquired.
        }
    }
}
