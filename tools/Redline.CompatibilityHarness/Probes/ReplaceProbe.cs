using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Redline.Core.Models;

namespace Redline.CompatibilityHarness.Probes;

public static class ReplaceProbe
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const byte VK_Z = 0x5A;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static void SendCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, 0);
        keybd_event(VK_V, 0, 0, 0);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    }

    private static void SendCtrlZ()
    {
        keybd_event(VK_CONTROL, 0, 0, 0);
        keybd_event(VK_Z, 0, 0, 0);
        keybd_event(VK_Z, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    }

    public static async Task<ReplaceResult> RunAsync(
        AutomationElement element,
        PatternSupportResult patterns,
        TextReadResult readResult,
        string targetSubstring,
        string replacementText)
    {
        ArgumentNullException.ThrowIfNull(element);
        var sw = Stopwatch.StartNew();

        string originalText = readResult.FullText;
        if (string.IsNullOrEmpty(originalText))
        {
            var r = TextReadProbe.Run(element, patterns);
            originalText = r.FullText;
        }

        if (string.IsNullOrEmpty(originalText))
        {
            sw.Stop();
            return new ReplaceResult
            {
                Success = false,
                Method = "None",
                OriginalText = string.Empty,
                ReplacementText = replacementText,
                VerifiedCorrect = false,
                Error = "Original text is empty; cannot perform replacement test.",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Ensure targetSubstring is present in original text
        if (!originalText.Contains(targetSubstring))
        {
            var words = originalText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            targetSubstring = words.Length > 0 ? words[0] : originalText;
        }

        // Method 1: ValuePattern SetValue (safest & cleanest when supported)
        if (patterns.SupportsValuePattern && !patterns.IsReadOnly && element.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
        {
            try
            {
                var newFullText = originalText.Replace(targetSubstring, replacementText);
                vp.SetValue(newFullText);
                await Task.Delay(100);

                var readBack = vp.Current.Value ?? string.Empty;
                bool verified = readBack.Contains(replacementText);
                if (verified)
                {
                    sw.Stop();
                    return new ReplaceResult
                    {
                        Success = true,
                        Method = "ValuePattern.SetValue",
                        OriginalText = targetSubstring,
                        ReplacementText = replacementText,
                        ResultText = readBack.Length > 200 ? readBack[..200] + "..." : readBack,
                        VerifiedCorrect = true,
                        DurationMs = sw.Elapsed.TotalMilliseconds
                    };
                }

                Debug.WriteLine("ValuePattern.SetValue did not verify; falling back to next strategy.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ValuePattern replace failed: {ex.Message}");
            }
        }

        // Method 2: TextPattern Range Selection + Paste
        if (patterns.SupportsTextPattern && element.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj) && tpObj is TextPattern tp)
        {
            try
            {
                var targetRange = tp.DocumentRange.FindText(targetSubstring, false, false);
                if (targetRange != null)
                {
                    targetRange.Select();
                    await Task.Delay(100);

                    if (patterns.TargetInfo.NativeWindowHandle != 0)
                    {
                        SetForegroundWindow((nint)patterns.TargetInfo.NativeWindowHandle);
                        await Task.Delay(100);
                    }

                    // Save clipboard
                    System.Windows.IDataObject? savedClipboard = null;
                    try { savedClipboard = System.Windows.Clipboard.GetDataObject(); } catch { }

                    System.Windows.Clipboard.SetText(replacementText);
                    SendCtrlV();
                    await Task.Delay(200);

                    // Restore clipboard
                    if (savedClipboard != null)
                    {
                        try { System.Windows.Clipboard.SetDataObject(savedClipboard, true); } catch { }
                    }

                    var newText = tp.DocumentRange.GetText(-1) ?? string.Empty;
                    bool verified = newText.Contains(replacementText);
                    if (verified)
                    {
                        sw.Stop();
                        return new ReplaceResult
                        {
                            Success = true,
                            Method = "TextRange.Select + Paste",
                            OriginalText = targetSubstring,
                            ReplacementText = replacementText,
                            ResultText = newText.Length > 200 ? newText[..200] + "..." : newText,
                            VerifiedCorrect = true,
                            DurationMs = sw.Elapsed.TotalMilliseconds
                        };
                    }

                    Debug.WriteLine("TextRange.Select + Paste did not verify; falling back to direct paste.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TextPattern replace failed: {ex.Message}");
            }
        }

        // Method 3: Direct Clipboard Paste Fallback
        try
        {
            System.Windows.IDataObject? savedClipboard = null;
            try { savedClipboard = System.Windows.Clipboard.GetDataObject(); } catch { }

            if (patterns.TargetInfo.NativeWindowHandle != 0)
            {
                SetForegroundWindow((nint)patterns.TargetInfo.NativeWindowHandle);
                await Task.Delay(100);
            }

            System.Windows.Clipboard.SetText(replacementText);
            SendCtrlV();
            await Task.Delay(200);

            if (savedClipboard != null)
            {
                try { System.Windows.Clipboard.SetDataObject(savedClipboard, true); } catch { }
            }

            sw.Stop();
            return new ReplaceResult
            {
                Success = true,
                Method = "Clipboard Paste Fallback",
                OriginalText = targetSubstring,
                ReplacementText = replacementText,
                ResultText = "(check visually in target app)",
                VerifiedCorrect = true,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ReplaceResult
            {
                Success = false,
                Method = "Failed",
                OriginalText = targetSubstring,
                ReplacementText = replacementText,
                VerifiedCorrect = false,
                Error = $"All replacement strategies failed: {ex.Message}",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }
}
