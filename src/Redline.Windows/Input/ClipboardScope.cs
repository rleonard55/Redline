using System.Runtime.InteropServices;
using Redline.Windows.Automation;

namespace Redline.Windows.Input;

/// <summary>
/// Temporarily puts text on the clipboard for a paste, then restores the previous contents (risk R5).
/// Only proceeds when the clipboard is empty or holds plain text — anything richer (images, files,
/// HTML, Office formats) can't be restored faithfully, so we refuse rather than lose it. The
/// temporary text is excluded from clipboard history and cloud sync.
/// </summary>
/// <remarks>
/// Uses the Win32 clipboard API with all formats rendered immediately (no delayed rendering), so
/// nothing depends on a message pump. All calls run on the UIA dispatcher thread, which owns the
/// message-only window used as clipboard owner.
/// </remarks>
public sealed class ClipboardScope
{
    private const uint CF_TEXT = 1;
    private const uint CF_OEMTEXT = 7;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_LOCALE = 16;

    private static readonly string[] PrivacyFormats =
        ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];

    /// <summary>
    /// Registered formats that can accompany plain text without carrying content of their own:
    /// other encodings of the same text (.NET publishes "System.String") and OLE clipboard
    /// bookkeeping ("DataObject", "Ole Private Data"), which any app copying via OLE adds.
    /// </summary>
    private static readonly string[] PlainTextCompanions =
        ["System.String", "UnicodeText", "Text", "OEMText", "Locale", "DataObject", "Ole Private Data"];

    private readonly UiaDispatcher _uia;
    private readonly string? _savedText; // null = clipboard was empty
    private uint _ourSequence;

    private ClipboardScope(UiaDispatcher uia, string? savedText)
    {
        _uia = uia;
        _savedText = savedText;
    }

    /// <summary>
    /// Saves the clipboard and replaces it with <paramref name="text"/>. Returns null (clipboard
    /// untouched) if its current contents can't be restored exactly or it can't be opened.
    /// </summary>
    public static Task<ClipboardScope?> TryReplaceAsync(UiaDispatcher uia, string text) => uia.InvokeAsync(() =>
    {
        if (!TryOpen()) return null;
        string? saved;
        try
        {
            if (!OnlyPlainText(out bool empty)) return null;
            saved = empty ? null : ReadUnicodeText();
            if (!empty && saved is null) return null;
            Write(text);
        }
        finally
        {
            CloseClipboard();
        }

        return new ClipboardScope(uia, saved) { _ourSequence = GetClipboardSequenceNumber() };
    });

    /// <summary>
    /// Puts the saved contents back — unless something else (the user, another app) has written to
    /// the clipboard since we did, in which case theirs wins.
    /// </summary>
    public Task<bool> RestoreAsync() => _uia.InvokeAsync(() =>
    {
        if (GetClipboardSequenceNumber() != _ourSequence) return false;
        if (!TryOpen()) return false;
        try
        {
            if (_savedText is null) EmptyClipboard();
            else Write(_savedText);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    });

    private static bool OnlyPlainText(out bool empty)
    {
        empty = true;
        for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
        {
            empty = false;
            if (format is CF_TEXT or CF_OEMTEXT or CF_UNICODETEXT or CF_LOCALE)
                continue;
            var name = FormatName(format);
            if (Array.IndexOf(PrivacyFormats, name) >= 0 || Array.IndexOf(PlainTextCompanions, name) >= 0)
                continue;
            return false;
        }
        return true;
    }

    private static string? ReadUnicodeText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return null;
        var ptr = GlobalLock(handle);
        try { return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr); }
        finally { GlobalUnlock(handle); }
    }

    /// <summary>Caller has the clipboard open. Replaces its contents with text plus privacy markers.</summary>
    private static void Write(string text)
    {
        EmptyClipboard();
        SetData(CF_UNICODETEXT, MemoryOf(text));
        foreach (var name in PrivacyFormats)
            SetData(RegisterClipboardFormat(name), new byte[4]); // DWORD 0 = "no" for history/cloud
    }

    private static byte[] MemoryOf(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    private static void SetData(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        var ptr = GlobalLock(handle);
        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == IntPtr.Zero)
            GlobalFree(handle); // ownership only transfers on success
    }

    private static string FormatName(uint format)
    {
        var buffer = new char[128];
        int length = GetClipboardFormatName(format, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>Another process may hold the clipboard briefly; retry for ~200 ms.</summary>
    private static bool TryOpen()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(Owner.Value)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    // EmptyClipboard with a null owner makes SetClipboardData fail, so own the clipboard with a
    // message-only window. Created lazily on the UIA thread (all callers run there).
    private static readonly ThreadLocal<IntPtr> Owner = new(() =>
        CreateWindowEx(0, "STATIC", "Redline clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, [Out] char[] lpszFormatName, int cchMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
}
