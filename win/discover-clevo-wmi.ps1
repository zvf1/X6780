<#
.SYNOPSIS
  Discovers the Windows WMI class generated for the Clevo keyboard-backlight
  control GUID, and shows you exactly how to call it.

.WHY
  tuxedo-drivers' clevo_wmi.c calls Linux's wmi_evaluate_method() against
  GUID ABBC0F6D-8EA1-11D1-00A0-C90629100000 with method_id = 0x27 (set
  white-only kb brightness) or 0x3D (get it). Windows' own ACPI/WMI mapper
  (wmiacpi.sys) reads the SAME firmware _WDG table and auto-generates a
  WMI class for this GUID under root\WMI -- but the BIOS vendor picks the
  class name and parameter names, so we have to look it up rather than
  hardcode it.

.USAGE
  Just run it (no admin needed for discovery):
    .\discover-clevo-wmi.ps1
#>

$TargetGuid = "{ABBC0F6D-8EA1-11D1-00A0-C90629100000}"

Write-Host "Looking for WMI classes under root\WMI with guid $TargetGuid ..." -ForegroundColor Cyan
Write-Host ""

$matches = Get-WmiObject -Namespace root\WMI -List | Where-Object {
    $_.Qualifiers["guid"] -and $_.Qualifiers["guid"].Value -eq $TargetGuid
}

if (-not $matches) {
    Write-Host "No class found with that exact guid qualifier." -ForegroundColor Yellow
    Write-Host "Possible reasons:"
    Write-Host "  - This board's BIOS doesn't expose a _WDG entry for this GUID at all"
    Write-Host "    (some boards only register the EVENT guid ABBC0F6B, not the METHOD guid ABBC0F6D)"
    Write-Host "  - Try also checking ABBC0F6B (event) and ABBC0F6C (email/string) just in case:"
    Write-Host ""
    Get-WmiObject -Namespace root\WMI -List | Where-Object {
        $_.Qualifiers["guid"] -and $_.Qualifiers["guid"].Value -match "ABBC0F6"
    } | ForEach-Object {
        Write-Host ("  Found related class: {0}  (guid {1})" -f $_.Name, $_.Qualifiers["guid"].Value)
    }
    exit 1
}

foreach ($class in $matches) {
    Write-Host "=== Class: $($class.Name) ===" -ForegroundColor Green
    Write-Host "Description: $($class.Qualifiers["Description"].Value)"
    Write-Host ""
    Write-Host "Methods (look for one whose WmiMethodId qualifier == 0x27 for SET / 0x3D for GET):"
    foreach ($method in $class.Methods) {
        $wmiId = $null
        try { $wmiId = $method.Qualifiers["WmiMethodId"].Value } catch {}
        $idHex = if ($wmiId -ne $null) { "0x{0:X2}" -f $wmiId } else { "?" }
        Write-Host ("  - {0}  (WmiMethodId = {1})" -f $method.Name, $idHex)
        foreach ($p in $method.InParameters.Properties) {
            Write-Host ("      in:  {0} ({1})" -f $p.Name, $p.Type)
        }
        if ($method.OutParameters) {
            foreach ($p in $method.OutParameters.Properties) {
                Write-Host ("      out: {0} ({1})" -f $p.Name, $p.Type)
            }
        }
    }
    Write-Host ""
    Write-Host "Instances:"
    Get-WmiObject -Namespace root\WMI -Class $class.Name | ForEach-Object {
        Write-Host "  - InstanceName: $($_.InstanceName)"
    }
    Write-Host ""
}

Write-Host "If you found a method with WmiMethodId 0x27, test setting kb brightness to level 1 with:" -ForegroundColor Cyan
Write-Host '  $cls = "<ClassNameFromAbove>"'
Write-Host '  $inst = Get-WmiObject -Namespace root\WMI -Class $cls | Select-Object -First 1'
Write-Host '  $inParams = $inst.GetMethodParameters("<MethodNameFromAbove>")'
Write-Host '  $inParams["Data"] = 1   # property name may differ -- check the "in:" lines above'
Write-Host '  $inst.InvokeMethod("<MethodNameFromAbove>", $inParams, $null)'
