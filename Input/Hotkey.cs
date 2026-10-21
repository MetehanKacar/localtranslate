using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace LocalTranslate.Input;

/// <summary>Global hotkeys such as "Ctrl+Alt+F9", registered on a message-only window.</summary>
internal sealed class Hotkey : IDisposable
{
    readonly HwndSource source;
    readonly HashSet<int> registered = new();

    public event Action<int>? Pressed;

    public Hotkey()
    {
        source = new HwndSource(new HwndSourceParameters("LocalTranslate.Hotkey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0
        });
        source.AddHook(WndProc);
    }

    public bool Register(int id, string text)
    {
        if (registered.Remove(id)) Native.UnregisterHotKey(source.Handle, id);
        if (!TryParse(text, out uint mods, out uint vk)) return false;
        if (!Native.RegisterHotKey(source.Handle, id, mods | Native.MOD_NOREPEAT, vk)) return false;
        registered.Add(id);
        return true;
    }

    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win" or "windows": modifiers |= Native.MOD_WIN; break;
                default:
                    // "1" must mean the digit key, not the enum value 1.
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse(name, true, out Key key) || int.TryParse(name, out _)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        // Require a modifier so a plain letter key isn't swallowed system-wide.
        return vk != 0 && (modifiers & (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_WIN)) != 0;
    }

    /// <summary>Formats a key press as hotkey text, or null for modifier-only presses.</summary>
    public static string? Format(ModifierKeys mods, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var keyName = key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString();
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && registered.Contains(wParam.ToInt32()))
        {
            Pressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in registered) Native.UnregisterHotKey(source.Handle, id);
        registered.Clear();
        source.Dispose();
    }
}
