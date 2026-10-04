# Prints the system DPI as a fresh DPI-aware process sees it (96 = 100%, 120 = 125%, 144 = 150%).
Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
public static class SystemDpiProbe {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
}
"@
[void][SystemDpiProbe]::SetProcessDPIAware()
[SystemDpiProbe]::GetDpiForSystem()
