Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
foreach ($h in @(11928766, 2033880)) {
  $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$h)
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
  foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    $v = "(no ValuePattern)"; $t = "(no TextPattern)"
    try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
    try { $t = $e.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(60) } catch {}
    $show = { param($s) if ($s.Length -le 25) { '"' + ($s -replace "`r|`n|`v",' ') + '"' } else { "<" + $s.Length + " chars>" } }
    "{0,-6} {1,-34} value={2,-22} text={3}" -f $(if ($h -eq 11928766) {"Outlk"} else {"Teams"}), $e.Current.ClassName.Substring(0,[Math]::Min(34,$e.Current.ClassName.Length)), (& $show $v), (& $show $t)
  }
}
