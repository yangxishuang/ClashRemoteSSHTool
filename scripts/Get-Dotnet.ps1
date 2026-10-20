$portableSdk = Join-Path $env:LOCALAPPDATA 'ClashOnWebSSH\tools\dotnet-10.0.402\dotnet.exe'
if ($env:CLASHSSH_DOTNET -and (Test-Path -LiteralPath $env:CLASHSSH_DOTNET)) { return $env:CLASHSSH_DOTNET }
if (Test-Path -LiteralPath $portableSdk) { return $portableSdk }
$availableSdk = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $availableSdk) { throw 'Install .NET SDK 10.0.402, or set CLASHSSH_DOTNET to its dotnet.exe.' }
return $availableSdk.Source
