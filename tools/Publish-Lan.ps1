param([string]$ServerUrl = 'http://127.0.0.1:61011/')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $taskRoot '.artifacts'
$publishRoot = Join-Path $artifactDirectory 'lan-publish'
$env:DOTNET_CLI_HOME = Join-Path $artifactDirectory 'dotnet-home'
$env:TEMP = Join-Path $artifactDirectory 'tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME,$env:TEMP | Out-Null
# Fresh directories prevent stale assemblies from an earlier deployment entering the client package.
$releaseDirectory = Join-Path $publishRoot ([DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$clientDirectory = Join-Path $releaseDirectory 'Client'
$serverDirectory = Join-Path $releaseDirectory 'Server'
& dotnet publish (Join-Path $taskRoot 'WpfArgumentMap/WpfArgumentMap.csproj') -c Release --nologo -o $clientDirectory
if ($LASTEXITCODE -ne 0) { throw 'WPF 发布失败。' }
& dotnet publish (Join-Path $taskRoot 'ArgumentMap.Server/ArgumentMap.Server.csproj') -c Release --nologo -o $serverDirectory
if ($LASTEXITCODE -ne 0) { throw 'Server 发布失败。' }
$address = [Uri]$ServerUrl
if (-not $address.IsAbsoluteUri -or $address.Scheme -notin @('http','https') -or $address.UserInfo -or $address.Query -or $address.Fragment -or $address.AbsolutePath -ne '/' -or $address.Host -in @('0.0.0.0','::','[::]')) { throw '请输入有效的 Server HTTP 地址。' }
@{ServerUrl=$address.AbsoluteUri} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $clientDirectory 'clientsettings.json')
$forbidden = Get-ChildItem -LiteralPath $clientDirectory -Recurse -File | Where-Object {
    $_.Name -match '(?i)ArgumentMap\.(Server|Storage|Business)|MySql|appsettings|Schema.*\.sql'
}
if ($forbidden) { throw ('客户端发布目录包含禁止文件：' + ($forbidden.Name -join ', ')) }
$dependencies = Get-Content -Raw -LiteralPath (Join-Path $clientDirectory 'WpfArgumentMap.deps.json')
if ($dependencies -match '(?i)MySqlConnector|ArgumentMap\.(Server|Storage|Business)|Microsoft.AspNetCore.App') {
    throw '客户端依赖清单仍包含服务器或数据库依赖。'
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'LAN-DEPLOYMENT.md') -Destination $releaseDirectory
Write-Output "发布完成：$releaseDirectory"
Write-Output 'Client 可分发到编辑电脑；Server 仅放在服务机。数据库私密配置不会自动复制，请按部署说明配置。'
