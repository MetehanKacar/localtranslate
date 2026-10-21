using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace LocalTranslate.Input;

/// <summary>
/// Last-resort selection reader for apps that don't expose their selection to UI
/// Automation: press Ctrl+C, read the clipboard, then put the user's previous
/// clipboard contents back.
/// </summary>
internal static class ClipboardGrabber
{
    // Ctrl+C means something else here (terminals: interrupt) or copies files /
    // cells with visible side effects.
    static readonly HashSet<string> SkipProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "wt",
        "mintty", "alacritty", "wezterm-gui", "putty", "kitty", "Hyper", "Tabby",
        "explorer", "EXCEL", "mspaint", "Photoshop", "LocalTranslate"
    };

    static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "VirtualConsoleClass"
    };

    public static async Task<string?> CopySelectionAsync()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || SkipClasses.Contains(Native.WindowClass(fg))) return null;

        Native.GetWindowThreadProcessId(fg, out uint pid);
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            if (SkipProcesses.Contains(proc.ProcessName)) return null;
        }
        catch (ArgumentException) { return null; }

        // With Shift/Alt/Win held, our Ctrl+C would become a different shortcut.
        if (Native.IsKeyDown(Native.VK_SHIFT) || Native.IsKeyDown(Native.VK_MENU) || Native.IsKeyDown(Native.VK_CONTROL) ||
            Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN))
            return null;

        var backup = Snapshot();
        uint before = Native.GetClipboardSequenceNumber();
        Native.SendKeyChord(Native.VK_CONTROL, 0x43 /* C */);

        for (int i = 0; i < 20 && Native.GetClipboardSequenceNumber() == before; i++)
            await Task.Delay(15);
        if (Native.GetClipboardSequenceNumber() == before) return null; // nothing was selected

        await Task.Delay(30); // let the source app finish writing every format
        string? text = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
                break;
            }
            catch (ExternalException) { await Task.Delay(25); } // clipboard busy
        }

        Restore(backup);
        return text;
    }

    static DataObject? Snapshot()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source == null) return null;
            var copy = new DataObject();
            foreach (var format in source.GetFormats(false))
            {
                try
                {
                    var data = source.GetData(format, false);
                    if (data != null) copy.SetData(format, data);
                }
                catch { /* some private formats can't be read back; skip them */ }
            }
            return copy;
        }
        catch (ExternalException) { return null; }
    }

    static void Restore(DataObject? backup)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (backup == null || backup.GetFormats().Length == 0) Clipboard.Clear();
                else Clipboard.SetDataObject(backup, true);
                return;
            }
            catch (ExternalException) { System.Threading.Thread.Sleep(25); }
        }
    }
}
