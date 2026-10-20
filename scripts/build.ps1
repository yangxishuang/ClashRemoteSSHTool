param([string]$RestoreSource, [switch]$NoPublish, [string]$OutputDirectory = 'artifacts/ClashOnWebSSH-0.2.1-win-x64')
$ErrorActionPreference = 'Stop'
$dotnetExe = & (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $restoreArgs = @()
    if ($RestoreSource) { $restoreArgs += @('--source', $RestoreSource, '-p:NuGetAudit=false') }
    & $dotnetExe build ClashOnWebSSH.sln -c Release --nologo @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $NoPublish) {
        & $dotnetExe publish src/ClashOnWebSSH.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded --nologo @restoreArgs -o $OutputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.md') -Force
        Copy-Item -LiteralPath docs/RELEASE-README.txt -Destination (Join-Path $OutputDirectory 'README.txt') -Force
        Copy-Item -LiteralPath licenses -Destination $OutputDirectory -Recurse -Force
        Write-Host "Ready: $OutputDirectory/ClashOnWebSSH.exe"
    }
} finally { Pop-Location }
