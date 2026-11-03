param([string]$RestoreSource, [switch]$NoPublish)
$ErrorActionPreference = 'Stop'
$dotnetExe = & (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
Push-Location (Split-Path $PSScriptRoot -Parent)
$publishLock = $null
$stagedPath = $null
try {
    $restoreArgs = @()
    if ($RestoreSource) { $restoreArgs += @('--source', $RestoreSource, '-p:NuGetAudit=false') }
    if (-not $NoPublish) {
        $projectPath = (Get-Location).Path
        $releasePath = Join-Path $projectPath 'artifacts'
        $releaseExe = Join-Path $releasePath 'Clash-Remote-SSH.exe'
        $toolsPath = Join-Path $projectPath '.tools'
        foreach ($directory in @($toolsPath, $releasePath)) {
            if (Test-Path -LiteralPath $directory) {
                $item = Get-Item -LiteralPath $directory -Force
                if ($item.FullName -ne $directory -or !$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw "发布目录必须是本项目内的普通目录：$directory"
                }
            }
        }
        New-Item -ItemType Directory -Force -Path $toolsPath | Out-Null
        $publishLock = [IO.File]::Open((Join-Path $toolsPath 'publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        $releasePrefix = $releasePath + [IO.Path]::DirectorySeparatorChar
        $runningRelease = Get-Process -Name Clash-Remote-SSH -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) }
        if ($runningRelease) { throw '请先关闭 artifacts 中运行的 ClashSSH遥控台，再更新发布产物。' }

        $publishId = [guid]::NewGuid().ToString('N')
        $stagedPath = Join-Path $toolsPath ("publish-" + $publishId)
        $previousPath = Join-Path $toolsPath ("previous-release-" + $publishId)
    }
    & $dotnetExe build Clash-Remote-SSH.sln -c Release --nologo @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $NoPublish) {
        & $dotnetExe publish src/Clash-Remote-SSH.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded --nologo @restoreArgs -o $stagedPath
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination (Join-Path $stagedPath 'THIRD-PARTY-NOTICES.md') -Force
        [xml]$projectXml = [IO.File]::ReadAllText((Join-Path $projectPath 'src/Clash-Remote-SSH.App/Clash-Remote-SSH.App.csproj'))
        $version = [string]$projectXml.Project.PropertyGroup.Version
        $releaseReadme = "ClashSSH遥控台 $version`r`n`r`nWindows x64 自包含单文件。双击 Clash-Remote-SSH.exe 启动。`r`n"
        [System.IO.File]::WriteAllText((Join-Path $stagedPath 'README.txt'), $releaseReadme, [System.Text.UTF8Encoding]::new($false))
        $licensePath = Join-Path $stagedPath 'licenses'
        New-Item -ItemType Directory -Force -Path $licensePath | Out-Null
        Get-ChildItem -LiteralPath licenses -File | Copy-Item -Destination $licensePath -Force
        $revision = & git rev-parse HEAD
        $dirty = [bool](& git status --porcelain)
        $buildInfo = @{ version = $version; commit = $revision; dirty = $dirty; runtime = 'win-x64'; builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json
        [System.IO.File]::WriteAllText((Join-Path $stagedPath 'BUILD-INFO.json'), $buildInfo, [System.Text.UTF8Encoding]::new($false))
        $checksums = Get-ChildItem -LiteralPath $stagedPath -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
            $relativeName = $_.FullName.Substring($stagedPath.TrimEnd('\').Length + 1).Replace('\', '/')
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relativeName
        }
        [System.IO.File]::WriteAllLines((Join-Path $stagedPath 'SHA256SUMS.txt'), [string[]]$checksums, [System.Text.UTF8Encoding]::new($false))
        $hasPrevious = Test-Path -LiteralPath $releasePath
        if ($hasPrevious) { [IO.Directory]::Move($releasePath, $previousPath) }
        try {
            [IO.Directory]::Move($stagedPath, $releasePath)
        } catch {
            if ($hasPrevious) { [IO.Directory]::Move($previousPath, $releasePath) }
            throw
        }
        if ($hasPrevious) { Remove-Item -LiteralPath $previousPath -Recurse -Force }
        Write-Host "Ready: $releaseExe (v$version)"
    }
} finally {
    try {
        if ($stagedPath -and (Test-Path -LiteralPath $stagedPath)) {
            Remove-Item -LiteralPath $stagedPath -Recurse -Force
        }
    } finally {
        if ($publishLock) { $publishLock.Dispose() }
        Pop-Location
    }
}
