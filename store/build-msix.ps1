# Builds the Microsoft Store package: store\out\UpBar_<version>_neutral.msix (upload that file to Partner Center;
# the Store signs it). -Register also installs the unpacked package locally to test it (needs Developer Mode).
#   powershell -ExecutionPolicy Bypass -File store\build-msix.ps1 [-Register]
param([switch]$Register)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot 'out'
$stage = Join-Path $out 'stage'

$id = Get-Content (Join-Path $PSScriptRoot 'identity.json') -Raw | ConvertFrom-Json
$m = [regex]::Match((Get-Content (Join-Path $root 'src\Backup.cs') -Raw), 'const string Version = "(\d+)\.(\d+)\.(\d+)"')
if (-not $m.Success) { throw 'Version not found in src\Backup.cs' }
$version = "$($m.Groups[1]).$($m.Groups[2]).$($m.Groups[3]).0" # the Store needs four parts, the last one 0

$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
if (-not $kit) { throw 'makeappx.exe not found: install the Windows SDK (App packaging tools)' }
$tools = $kit.DirectoryName

Write-Host "Building Upbar.exe $version"
& cmd /c (Join-Path $root 'build.bat')
if ($LASTEXITCODE) { throw 'build.bat failed' }
$exe = Join-Path $root 'Upbar.exe'
$t = Start-Process $exe -ArgumentList '--selftest' -Wait -PassThru # GUI app: wait for its exit code explicitly
if ($t.ExitCode) { throw "Self-test failed: $($t.ExitCode) failing check(s)" }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe, (Join-Path $root 'upbar.ico'), (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'fonts') $stage -Recurse
Copy-Item (Join-Path $root 'weather') $stage -Recurse
Start-Process $exe -ArgumentList '--make-store-assets', "`"$(Join-Path $stage 'Assets')`"" -Wait

$manifest = (Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw).
    Replace('$NAME$', $id.Name).Replace('$PUBLISHER$', $id.Publisher).Replace('$PUBLISHERNAME$', $id.PublisherDisplayName).Replace('$VERSION$', $version)
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

# resources.pri lets Windows pick the right logo size for each display scale
& "$tools\makepri.exe" createconfig /cf "$out\priconfig.xml" /dq en-US /o | Out-Null
& "$tools\makepri.exe" new /pr $stage /cf "$out\priconfig.xml" /mn "$stage\AppxManifest.xml" /of "$stage\resources.pri" /o | Out-Null
if ($LASTEXITCODE) { throw 'makepri failed' }

$msix = Join-Path $out "UpBar_$($version)_neutral.msix"
& "$tools\makeappx.exe" pack /d $stage /p $msix /o | Out-Null
if ($LASTEXITCODE) { throw 'makeappx failed' }
Write-Host "Package: $msix ($([math]::Round((Get-Item $msix).Length / 1KB)) KB)"

if ($Register) {
    Get-AppxPackage -Name $id.Name | Remove-AppxPackage
    Add-AppxPackage -Register (Join-Path $stage 'AppxManifest.xml')
    Write-Host "Installed $($id.Name) for testing: start UpBar from the Start menu"
}
