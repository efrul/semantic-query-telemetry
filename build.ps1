# Builds dist\ZayitTelemetry.exe: one file, with Zayit's font and pictures inside, on the .NET Framework 4.8 that comes
# with Windows 10 and 11 (its compiler is part of Windows too). -Install also copies it to
# %LOCALAPPDATA%\Programs\Zayit Telemetry and adds a Start menu shortcut.
param(
    [switch]$Install,
    [string]$FeedbackUrl = $env:PARDES_FEEDBACK_URL
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force "$root\dist" | Out-Null

$refs = @("/r:$fw\WPF\PresentationFramework.dll", "/r:$fw\WPF\PresentationCore.dll", "/r:$fw\WPF\WindowsBase.dll",
          "/r:$fw\System.Xaml.dll", "/r:System.Core.dll", "/r:System.dll")
$res = 'notoserifhebrew.ttf', 'wallpaper.jpg', 'wallpaper_dark.jpg', 'branch.png', 'app.png' | ForEach-Object { "/resource:$root\assets\$_,$_" }

$urlTemp = $null
if (-not [string]::IsNullOrWhiteSpace($FeedbackUrl)) {
    $urlTemp = "$root\dist\feedback-url.txt"
    [System.IO.File]::WriteAllText($urlTemp, $FeedbackUrl.Trim(), [System.Text.Encoding]::UTF8)
    $res += "/resource:$urlTemp,feedback-url.txt"
}

$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
try {
    & "$fw\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe /platform:anycpu "/win32icon:$root\assets\app.ico" "/win32manifest:$root\src\app.manifest" `
        @refs @res "/out:$root\dist\ZayitTelemetry.exe" @src
    if ($LASTEXITCODE) { throw "build failed" }
} finally {
    if ($urlTemp -and (Test-Path $urlTemp)) { Remove-Item $urlTemp -Force -ErrorAction SilentlyContinue }
}

Get-Item "$root\dist\ZayitTelemetry.exe" | ForEach-Object { "{0}  ({1:N2} MB)" -f $_.FullName, ($_.Length / 1MB) }

if ($Install) {
    $dir = "$env:LOCALAPPDATA\Programs\Zayit Telemetry"
    Get-Process ZayitTelemetry -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item "$root\dist\ZayitTelemetry.exe" "$dir\ZayitTelemetry.exe" -Force
    $lnk = "$([Environment]::GetFolderPath('Programs'))\שליחת נתוני חיפוש — זית ואוצריא.lnk"
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sh.TargetPath = "$dir\ZayitTelemetry.exe"
    $sh.WorkingDirectory = $dir
    $sh.Description = 'שליחת היסטוריית החיפושים והמשובים מזית ומאוצריא לשיפור החיפוש הסמנטי של פרדס'
    $sh.Save()
    "installed: $dir\ZayitTelemetry.exe, shortcut in the Start menu"
}
