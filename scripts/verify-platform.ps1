[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$launcher = Join-Path $PSScriptRoot 'start-demo.ps1'
$dataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("invoice-review-platform-" + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $dataRoot 'published-app'
$databasePath = Join-Path $dataRoot 'invoices.db'
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Get-AvailableLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Start-DemoProcess([int]$Port) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'pwsh'
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($launcher)
    $startInfo.ArgumentList.Add('-Port')
    $startInfo.ArgumentList.Add($Port.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    $startInfo.ArgumentList.Add('-PublishOutput')
    $startInfo.ArgumentList.Add($publishRoot)
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['Extraction__Profile'] = 'Deterministic'
    $startInfo.Environment['Storage__RootPath'] = $dataRoot

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'The documented demonstration launcher could not be started.'
    }

    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()
    $processes.Add($process)
    return $process
}

function Wait-ForHealthy([System.Diagnostics.Process]$Process, [Uri]$HealthUri) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "The demonstration host exited before becoming healthy (exit code $($Process.ExitCode))."
        }

        try {
            $response = Invoke-WebRequest -Uri $HealthUri -TimeoutSec 2 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            # Connection refusal and request timeout are expected while publish and
            # native startup preflight are still in progress.
        }

        Start-Sleep -Milliseconds 250
    }

    throw "The demonstration host did not become healthy at the loopback endpoint."
}

function Stop-DemoProcess([System.Diagnostics.Process]$Process) {
    if (-not $Process.HasExited) {
        $Process.Kill($true)
        $Process.WaitForExit()
    }
}

try {
    foreach ($command in 'dotnet', 'node', 'npm', 'pwsh') {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "Required command '$command' is not available on PATH."
        }
    }

    New-Item -ItemType Directory -Path $dataRoot | Out-Null
    $port = Get-AvailableLoopbackPort
    $baseUri = [Uri]"http://127.0.0.1:$port/"

    $first = Start-DemoProcess -Port $port
    Wait-ForHealthy -Process $first -HealthUri ([Uri]::new($baseUri, 'health'))
    $deepLink = Invoke-WebRequest -Uri ([Uri]::new($baseUri, 'invoices/synthetic-deep-link')) -TimeoutSec 5 -SkipHttpErrorCheck
    $deepLinkContent = if ($deepLink.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($deepLink.Content)
    }
    else {
        [string]$deepLink.Content
    }
    if ($deepLink.StatusCode -ne 200 -or $deepLinkContent -notmatch '<div id="root">') {
        throw 'The published SPA deep-link fallback did not serve the production client.'
    }
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf) -or (Get-Item -LiteralPath $databasePath).Length -eq 0) {
        throw 'The production startup path did not create a non-empty SQLite database.'
    }
    $databaseLength = (Get-Item -LiteralPath $databasePath).Length
    Stop-DemoProcess -Process $first

    $second = Start-DemoProcess -Port $port
    Wait-ForHealthy -Process $second -HealthUri ([Uri]::new($baseUri, 'health'))
    $queue = Invoke-RestMethod -Uri ([Uri]::new($baseUri, 'api/invoices?page=1&pageSize=25')) -TimeoutSec 5
    if ($null -eq $queue.items -or $queue.totalItems -ne 0) {
        throw 'The restarted application did not read the expected persisted SQLite state.'
    }
    if ((Get-Item -LiteralPath $databasePath).Length -lt $databaseLength) {
        throw 'The SQLite database regressed during the controlled restart.'
    }
    Stop-DemoProcess -Process $second

    Write-Host "PASS platform=$([System.Runtime.InteropServices.RuntimeInformation]::OSDescription) architecture=$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
    Write-Host 'PASS documented launcher, Release publish, PDFium preflight, loopback health, SPA deep link, SQLite migration, and controlled restart'
}
finally {
    foreach ($process in $processes) {
        try { Stop-DemoProcess -Process $process } catch { }
        $process.Dispose()
    }

    if (Test-Path -LiteralPath $dataRoot) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
}
