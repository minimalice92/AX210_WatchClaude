# Builds AX210_WatchClaude.exe and, if Inno Setup 6 is installed, the installer.
#   .\build.ps1             app + icon assets + installer  ->  dist\
#   .\build.ps1 -AppOnly    just the app                   ->  build\AX210_WatchClaude.exe
# Needs: Visual Studio 2019+/Build Tools (Roslyn csc), .NET Framework 4.8; Inno Setup 6.4+ for the installer.
param([switch]$AppOnly)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot; $src = "$here\src"; $out = "$here\build"; $dist = "$here\dist"
New-Item -ItemType Directory -Force $out, $dist | Out-Null

$csc = Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe", "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $csc) { throw "Roslyn csc.exe not found (install Visual Studio or the Build Tools with the .NET desktop workload)." }
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

# 1. icon + installer artwork, drawn from src\Brand.cs
& $csc /nologo /target:exe /optimize+ /langversion:7.3 /out:"$out\make-assets.exe" /r:"$fw\System.Drawing.dll" "$here\tools\MakeAssets.cs" "$src\Brand.cs"
if ($LASTEXITCODE -ne 0) { throw "Asset tool build failed." }
& "$out\make-assets.exe" "$here\assets" "$here\docs" | Out-Null

# 2. the app (icon embedded for Explorer/Task Manager, and as a resource for the windows)
$version = (Select-String -Path "$src\AX210_WatchClaude.cs" -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)').Matches[0].Groups[1].Value
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /langversion:7.3 /warn:0 `
    /out:"$out\AX210_WatchClaude.exe" /win32manifest:"$src\app.manifest" /win32icon:"$here\assets\app.ico" `
    /resource:"$here\assets\app.ico,AX210WatchClaude.app.ico" `
    /r:"$fw\System.dll" /r:"$fw\System.Core.dll" /r:"$fw\System.Windows.Forms.dll" /r:"$fw\System.Drawing.dll" /r:"$fw\System.Web.Extensions.dll" `
    "$src\AX210_WatchClaude.cs" "$src\Features.cs" "$src\UI.cs" "$src\Settings.cs" "$src\Setup.cs" "$src\Brand.cs"
if ($LASTEXITCODE -ne 0) { throw "App build failed." }
Write-Host "Built $out\AX210_WatchClaude.exe ($version)"
if ($AppOnly) { return }

# 3. installer
$iscc = @("${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "${env:ProgramFiles}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Warning "Inno Setup 6 not found; skipped the installer (winget install JRSoftware.InnoSetup)."; return }
& $iscc /Q "/DAppVersion=$version" "/O$dist" "$here\installer\AX210_WatchClaude.iss"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
Write-Host "Built $dist\AX210_WatchClaude-Setup-$version.exe"
