# Builds OverlayKit's native parts (overlaykit64.dll and the test host) into overlay\bin.
# Needs Visual Studio or its Build Tools with the C++ workload and a Windows 10/11 SDK.
param([switch]$HostOnly)
$ErrorActionPreference = 'Stop'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild with the C++ tools was not found (install the "Desktop development with C++" workload).' }

$projects = @("$PSScriptRoot\native\OverlayKit\OverlayKit.vcxproj")
if (-not $HostOnly) { $projects += "$PSScriptRoot\native\TestHost\TestHost.vcxproj" }
foreach ($project in $projects) {
    & $msbuild $project /nologo /v:minimal /p:Configuration=Release /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
