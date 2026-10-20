param(
    [ValidateSet('All', 'Unit', 'Http', 'MySql', 'Rendering')]
    [string]$Suite = 'All'
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$artifactDirectory = Join-Path $taskRoot '.artifacts'
$resultDirectory = Join-Path $artifactDirectory 'test-results'
$buildDirectory = Join-Path $artifactDirectory 'test-build'
$environmentPaths = @{
    DOTNET_CLI_HOME = Join-Path $artifactDirectory 'dotnet-home'
    NUGET_HTTP_CACHE_PATH = Join-Path $artifactDirectory 'nuget-cache'
    TEMP = Join-Path $artifactDirectory 'tmp'
    TMP = Join-Path $artifactDirectory 'tmp'
}
$previousEnvironment = @{}
foreach ($name in $environmentPaths.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    New-Item -ItemType Directory -Path $environmentPaths[$name] -Force | Out-Null
    [Environment]::SetEnvironmentVariable($name, $environmentPaths[$name], 'Process')
}
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

Push-Location -LiteralPath $taskRoot
try {
    $testArguments = @(
        'test', (Join-Path $taskRoot 'ArgumentMap.Tests\ArgumentMap.Tests.csproj'), '--nologo',
        "-p:OutputPath=$buildDirectory/", '--results-directory', $resultDirectory,
        '--logger', "trx;LogFileName=argumentmap-$($Suite.ToLowerInvariant()).trx",
        '--logger', 'console;verbosity=minimal'
    )
    if ($Suite -ne 'All') { $testArguments += @('--filter', "TestCategory=$Suite") }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw "测试失败，退出码 $LASTEXITCODE。报告位于 $resultDirectory。" }
}
finally {
    Pop-Location
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}
