using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LocalTranslate.Input;

/// <summary>
/// System-wide mouse button / wheel events (WH_MOUSE_LL). The callback runs on
/// the UI thread inside the input pipeline, so it only queues work and returns;
/// a slow hook would make the mouse lag everywhere.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    public event Action<Native.POINT>? LeftDown;
    public event Action<Native.POINT>? LeftUp;
    public event Action<Native.POINT>? OtherDown;
    public event Action? Wheel;

    readonly Native.LowLevelProc proc; // kept in a field so the GC can't collect the delegate
    readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    IntPtr hook;

    public MouseHook()
    {
        proc = Callback;
        hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var pt = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam).pt;
            Action? handler = (int)wParam switch
            {
                Native.WM_LBUTTONDOWN => () => LeftDown?.Invoke(pt),
                Native.WM_LBUTTONUP => () => LeftUp?.Invoke(pt),
                Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN => () => OtherDown?.Invoke(pt),
                Native.WM_MOUSEWHEEL or Native.WM_MOUSEHWHEEL => () => Wheel?.Invoke(),
                _ => null
            };
            if (handler != null) dispatcher.BeginInvoke(handler);
        }
        return Native.CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }
}
