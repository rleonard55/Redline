Add-Type @"
using System; using System.Runtime.InteropServices;
public static class H {
  [DllImport("user32.dll", SetLastError=true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
  [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
}
"@
$candidates = @(
  @("Ctrl+Alt+Space", 0x3, 0x20), @("Ctrl+Shift+Space", 0x6, 0x20), @("Alt+Shift+Space", 0x5, 0x20),
  @("Ctrl+Alt+Period", 0x3, 0xBE), @("Ctrl+Shift+Period", 0x6, 0xBE), @("Ctrl+Alt+Semicolon", 0x3, 0xBA),
  @("Ctrl+Alt+Enter", 0x3, 0x0D), @("Ctrl+Shift+F12", 0x6, 0x7B), @("Win+Alt+Space", 0x9, 0x20), @("Ctrl+Alt+Slash", 0x3, 0xBF)
)
$id = 100
foreach ($c in $candidates) {
  $ok = [H]::RegisterHotKey([IntPtr]::Zero, $id, [uint32]($c[1] -bor 0x4000), [uint32]$c[2])
  "{0,-22} {1}" -f $c[0], $(if ($ok) { "free" } else { "TAKEN" })
  if ($ok) { [void][H]::UnregisterHotKey([IntPtr]::Zero, $id) }
  $id++
}
