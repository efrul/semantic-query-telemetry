# Pictures of the window without showing it (see offscreen.cs for the script commands).
#   .\tools\offscreen.ps1 -Script steps.txt -Out shots [-Theme dark|light] [-Exe path]
param([Parameter(Mandatory)][string]$Script, [Parameter(Mandatory)][string]$Out, [string]$Theme = "light", [string]$Exe = "")
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
$dir = "$env:TEMP\zayittelemetry-offscreen"
New-Item -ItemType Directory -Force $dir, $Out | Out-Null
if (-not $Exe) { $Exe = "$repo\dist\ZayitTelemetry.exe" }
Copy-Item $Exe "$dir\ZayitTelemetry.exe" -Force

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
& "$fw\csc.exe" /nologo /codepage:65001 /target:exe "/out:$dir\offscreen.exe" `
    "/r:$fw\WPF\PresentationFramework.dll" "/r:$fw\WPF\PresentationCore.dll" "/r:$fw\WPF\WindowsBase.dll" "/r:$fw\System.Xaml.dll" "/r:System.Core.dll" `
    "$PSScriptRoot\offscreen.cs"
if ($LASTEXITCODE) { throw "offscreen.cs did not compile" }

$env:ZAYITTELEMETRY_THEME = $Theme
try { & "$dir\offscreen.exe" (Resolve-Path $Out).Path (Resolve-Path $Script).Path }
finally {
    $env:ZAYITTELEMETRY_THEME = $null
}
