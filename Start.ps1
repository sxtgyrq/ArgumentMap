param(
    [ValidateSet('All', 'Server', 'Client')]
    [string]$Target = 'All',
    [string]$SettingsPath = $env:ARGUMENTMAP_MYSQL_CONFIG,
    [ValidateRange(1, 65535)]
    [int]$Port = 61011,
    [ValidateRange(1, 3600)]
    [int]$StartupTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if (-not $SettingsPath) {
    $SettingsPath = Join-Path $taskRoot 'ArgumentMap.Server\appsettings.Local.json'
}
if ($Target -ne 'Client' -and -not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
    throw "Database settings not found: $SettingsPath. Copy ArgumentMap.Server/appsettings.Local.example.json to appsettings.Local.json and configure MySQL, or pass -SettingsPath."
}
if ($Target -ne 'Client') { $SettingsPath = (Resolve-Path -LiteralPath $SettingsPath).Path }

function Test-ServerPort {
    $connection = New-Object System.Net.Sockets.TcpClient
    try {
        $pending = $connection.ConnectAsync('127.0.0.1', $Port)
        return ($pending.Wait(500) -and $connection.Connected)
    }
    catch { return $false }
    finally { $connection.Dispose() }
}

if ($Target -ne 'Client' -and (Test-ServerPort)) {
    throw "Port $Port is already in use. Stop the existing server first, or use -Port with another port."
}

$projects = switch ($Target) {
    'Server' { 'ArgumentMap.Server' }
    'Client' { 'WpfArgumentMap' }
    'All' { 'ArgumentMap.Server'; 'WpfArgumentMap' }
}
foreach ($project in $projects) {
    & dotnet build (Join-Path $taskRoot "$project\$project.csproj") --configuration Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

$logDirectory = Join-Path $taskRoot '.artifacts\logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$outputLog = Join-Path $logDirectory "server-$stamp.log"
$errorLog = Join-Path $logDirectory "server-$stamp.error.log"
$serverExe = Join-Path $taskRoot 'ArgumentMap.Server\bin\Debug\net8.0\ArgumentMap.Server.exe'
$clientExe = Join-Path $taskRoot 'WpfArgumentMap\bin\Debug\net8.0-windows\WpfArgumentMap.exe'
$clientSettings = Join-Path (Split-Path $clientExe) 'clientsettings.json'
# Only change the generated client configuration; source settings stay intact.
if ($Target -ne 'Server') {
    @{ ServerUrl = "http://127.0.0.1:$Port/" } | ConvertTo-Json | Set-Content -LiteralPath $clientSettings -Encoding UTF8
}
if ($Target -eq 'Client') {
    Start-Process -FilePath $clientExe -WorkingDirectory (Split-Path $clientExe) | Out-Null
    Write-Host 'WPF started. Connect to the running Server from the login window.'
    return
}

$serverProcess = Start-Process -FilePath $serverExe -WorkingDirectory $taskRoot `
    -ArgumentList @('--urls', "http://0.0.0.0:$Port", '--settings', ('"' + $SettingsPath + '"')) `
    -WindowStyle Hidden -RedirectStandardOutput $outputLog -RedirectStandardError $errorLog -PassThru
Write-Host "Starting Server (PID $($serverProcess.Id)). Logs: $logDirectory"
$timer = [Diagnostics.Stopwatch]::StartNew()
while ($true) {
    $serverProcess.Refresh()
    if ($serverProcess.HasExited) {
        throw "Server exited with code $($serverProcess.ExitCode). See $errorLog and $outputLog"
    }
    if (Test-ServerPort) { break }
    if ($timer.Elapsed.TotalSeconds -ge $StartupTimeoutSeconds) {
        throw "Server startup timed out. PID $($serverProcess.Id) is still running; inspect $errorLog and $outputLog. WPF was not started."
    }
    Start-Sleep -Milliseconds 300
}

Write-Host "Server ready: http://127.0.0.1:$Port/ (PID $($serverProcess.Id))"
if ($Target -eq 'All') {
    $clientProcess = Start-Process -FilePath $clientExe -WorkingDirectory (Split-Path $clientExe) -PassThru
    Write-Host "WPF started (PID $($clientProcess.Id)). Closing WPF leaves Server running."
}
Write-Host "To stop Server: Stop-Process -Id $($serverProcess.Id)"
