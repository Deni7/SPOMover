<#
.SYNOPSIS
  Release of SPOMover: tests, the app and the CLI published with their own .NET (win-x64),
  the MSI installer and a portable zip.

.EXAMPLE
  .\installer\build.ps1
  .\installer\build.ps1 -Version 1.0.1 -SkipTests

  Result in out\release:
    SPOMover-<version>-x64.msi           installer (Program Files, Start menu shortcut)
    SPOMover-<version>-x64-portable.zip  the same folder without installation
    SHA256SUMS.txt
#>
param(
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'out\release'
$app = Join-Path $out 'files\SPOMover'
$cli = Join-Path $out 'files\cli'

function Invoke-Step([string]$title, [scriptblock]$action) {
    Write-Host "==> $title" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$title — код виходу $LASTEXITCODE" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Версія '$Version' має бути у вигляді 1.2.3 (так її розуміє MSI)." }

if (Test-Path $out) { Remove-Item $out -Recurse -Force -Confirm:$false }

if (-not $SkipTests) {
    # One test project at a time: the UI responsiveness test has a time limit.
    Invoke-Step 'Тести' { dotnet test (Join-Path $root 'SPOMover.sln') -c Release --nologo -m:1 }
}

# Packaging=true builds every project for win-x64 with its own runtime (see Directory.Build.props).
Invoke-Step 'Застосунок' { dotnet publish (Join-Path $root 'src\SPOMover.Desktop') -c Release -p:Packaging=true -p:Version=$Version -o $app --nologo }
Invoke-Step 'CLI' { dotnet publish (Join-Path $root 'src\SPOMover.Cli') -c Release -p:Packaging=true -p:Version=$Version -o $cli --nologo }

# The CLI goes next to the app and uses its runtime: only its own files are added, the app's are kept
# (the app's runtime is a superset: it also carries the Windows Desktop libraries).
Get-ChildItem $cli -Recurse -File | ForEach-Object {
    $target = Join-Path $app $_.FullName.Substring($cli.Length + 1)
    if (-not (Test-Path $target)) {
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $_.FullName $target
    }
}
Remove-Item $cli -Recurse -Force -Confirm:$false
New-Item -ItemType Directory -Force (Join-Path $app 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\USER_GUIDE.md') (Join-Path $app 'docs')
Copy-Item (Join-Path $root 'docs\USER_GUIDE.uk.md') (Join-Path $app 'docs')
Copy-Item (Join-Path $root 'README.md') (Join-Path $app 'docs')

Invoke-Step 'Інсталятор' { dotnet build (Join-Path $PSScriptRoot 'SPOMover.Installer.wixproj') -c Release -p:Version=$Version -p:PayloadDir=$app -o $out --nologo }
Get-ChildItem $out -Filter *.wixpdb | Remove-Item -Confirm:$false

Write-Host '==> Портативний zip' -ForegroundColor Cyan
Compress-Archive -Path $app -DestinationPath (Join-Path $out "SPOMover-$Version-x64-portable.zip") -CompressionLevel Optimal

Get-ChildItem $out -File | Where-Object Extension -in '.msi', '.zip' | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content (Join-Path $out 'SHA256SUMS.txt')

Write-Host "`nГотово: $out" -ForegroundColor Green
Get-ChildItem $out -File | Format-Table Name, @{ n = 'МБ'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
