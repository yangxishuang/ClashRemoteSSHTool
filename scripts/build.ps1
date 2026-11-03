param([string]$RestoreSource, [switch]$NoPublish, [string]$OutputDirectory = 'artifacts/Clash-Remote-SSH')
$ErrorActionPreference = 'Stop'
$dotnetExe = & (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $restoreArgs = @()
    if ($RestoreSource) { $restoreArgs += @('--source', $RestoreSource, '-p:NuGetAudit=false') }
    $releasePath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory)) }
    $releaseExe = Join-Path $releasePath 'Clash-Remote-SSH.exe'
    if (-not $NoPublish) {
        $runningRelease = Get-Process -Name Clash-Remote-SSH -ErrorAction SilentlyContinue | Where-Object Path -eq $releaseExe
        if ($runningRelease) { throw '请先关闭固定发布目录中的 ClashSSH遥控台再构建；不会自动终止你的窗口。' }
    }
    & $dotnetExe build Clash-Remote-SSH.sln -c Release --nologo @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $NoPublish) {
        & $dotnetExe publish src/Clash-Remote-SSH.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded --nologo @restoreArgs -o $releasePath
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination (Join-Path $releasePath 'THIRD-PARTY-NOTICES.md') -Force
        [xml]$projectXml = Get-Content -LiteralPath src/Clash-Remote-SSH.App/Clash-Remote-SSH.App.csproj
        $version = [string]$projectXml.Project.PropertyGroup.Version
        $releaseReadme = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'docs/RELEASE-README.txt')) -replace '(?m)^ClashSSH遥控台 [^\r\n]+', ('ClashSSH遥控台 ' + $version)
        [System.IO.File]::WriteAllText((Join-Path $releasePath 'README.txt'), $releaseReadme, [System.Text.UTF8Encoding]::new($false))
        $licensePath = Join-Path $releasePath 'licenses'
        New-Item -ItemType Directory -Force -Path $licensePath | Out-Null
        Get-ChildItem -LiteralPath licenses -File | Copy-Item -Destination $licensePath -Force
        $revision = & git rev-parse HEAD
        $dirty = [bool](& git status --porcelain)
        $buildInfo = @{ version = $version; commit = $revision; dirty = $dirty; runtime = 'win-x64'; builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json
        [System.IO.File]::WriteAllText((Join-Path $releasePath 'BUILD-INFO.json'), $buildInfo, [System.Text.UTF8Encoding]::new($false))
        $checksums = Get-ChildItem -LiteralPath $releasePath -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
            $relativeName = $_.FullName.Substring($releasePath.TrimEnd('\').Length + 1).Replace('\', '/')
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relativeName
        }
        [System.IO.File]::WriteAllLines((Join-Path $releasePath 'SHA256SUMS.txt'), [string[]]$checksums, [System.Text.UTF8Encoding]::new($false))
        Compress-Archive -Path (Join-Path $releasePath '*') -DestinationPath ($releasePath + '.zip') -Force -CompressionLevel Optimal
        Write-Host "Ready: $releaseExe (v$version)"
    }
} finally { Pop-Location }
