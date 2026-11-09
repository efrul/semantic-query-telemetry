# Builds dist\SearchDataSender.exe: one file, with its font and pictures inside, on the .NET Framework 4.8 that comes
# with Windows 10 and 11 (its C# compiler is part of Windows too, and takes C# 5). The core's tests are built in, for
# SearchDataSender.exe --selftest; on Linux they run with tools/test-core.sh.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force "$root\dist" | Out-Null

$refs = @("/r:$fw\WPF\PresentationFramework.dll", "/r:$fw\WPF\PresentationCore.dll", "/r:$fw\WPF\WindowsBase.dll",
          "/r:$fw\System.Xaml.dll", "/r:System.Core.dll", "/r:System.dll")
$res = 'notoserifhebrew.ttf', 'wallpaper.jpg', 'wallpaper_dark.jpg', 'branch.png', 'app.png' | ForEach-Object { "/resource:$root\assets\$_,$_" }
$src = @(Get-ChildItem "$root\src\Core\*.cs", "$root\src\App\*.cs" | ForEach-Object { $_.FullName }) +
       @(Get-ChildItem "$root\tests\*.cs" | Where-Object { $_.Name -ne 'LinuxMain.cs' } | ForEach-Object { $_.FullName })

& "$fw\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe /platform:anycpu "/win32icon:$root\assets\app.ico" `
    "/win32manifest:$root\src\App\app.manifest" @refs @res "/out:$root\dist\SearchDataSender.exe" @src
if ($LASTEXITCODE) { throw "build failed" }
Write-Host "dist\SearchDataSender.exe"
