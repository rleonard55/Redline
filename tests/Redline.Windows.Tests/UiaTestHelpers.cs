using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Redline.Windows.Tests;

internal static class UiaTestHelpers
{
    /// <summary>
    /// <see cref="AutomationElement.FromHandle"/> for a window the test just created. UIA sometimes isn't
    /// ready for a brand-new window yet and throws a COMException (seen on CI and occasionally locally),
    /// so retry briefly. Call on the UIA thread.
    /// </summary>
    public static AutomationElement FromHandle(IntPtr hwnd)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return AutomationElement.FromHandle(hwnd);
            }
            catch (COMException) when (attempt < 10)
            {
                Thread.Sleep(200);
            }
        }
    }
}
