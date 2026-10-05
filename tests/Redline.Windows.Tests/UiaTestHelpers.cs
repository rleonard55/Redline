using System.Runtime.InteropServices;
using System.Windows.Automation;
using Redline.Windows.Automation;

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

    /// <summary>
    /// <see cref="FromHandle"/> plus <see cref="ElementInfo.Capture"/>, retried as a whole: under load (the full
    /// test run, parallel test classes) reading a brand-new window's properties can also fail once with
    /// ElementNotAvailableException. Call on the UIA thread.
    /// </summary>
    public static (AutomationElement Element, ElementInfo Info) Capture(IntPtr hwnd)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var element = FromHandle(hwnd);
                return (element, ElementInfo.Capture(element));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException && attempt < 10)
            {
                Thread.Sleep(200);
            }
        }
    }
}
