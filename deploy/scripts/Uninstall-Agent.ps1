#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes Worker and Watchdog Windows Services after a server-issued uninstall grant.

.DESCRIPTION
    Requests an uninstall grant from the API, waits for an administrator to approve it in
    /admin, consumes the one-time code shown to this PC, then removes the services.
    Uninstall does not proceed until the grant is approved and consumed.

.PARAMETER ServerBaseUrl
    API base URL. Must be HTTPS unless it is loopback HTTP for diagnostics.

.PARAMETER ApiToken
    Agent bearer token. Same value as API Security:AgentToken.

.PARAMETER DeviceCode
    PC identity used when requesting the grant. Defaults to the machine name.

.PARAMETER InstallRoot
    Parent install directory. Agent folders under this path are removed unless -KeepFiles is set.

.PARAMETER StateRoot
    ProgramData root. Deleted only when -RemoveState is specified.

.PARAMETER KeepFiles
    Leave install directories in place after deleting the services.

.PARAMETER RemoveState
    Also delete the snapshot queue, health file, staging, and backup directories.

.PARAMETER PollSeconds
    Seconds between grant status checks. Default 3.

.PARAMETER TimeoutMinutes
    How long to wait for administrator approval. Default 120.

.EXAMPLE
    .\Uninstall-Agent.ps1 -ServerBaseUrl https://license-watcher.contoso.local -ApiToken $token

.EXAMPLE
    .\Uninstall-Agent.ps1 -ServerBaseUrl https://license-watcher.contoso.local -ApiToken $token -RemoveState
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ServerBaseUrl,

    [Parameter(Mandatory)]
    [string] $ApiToken,

    [string] $DeviceCode = $env:COMPUTERNAME,

    [string] $InstallRoot = "C:\Program Files\SwLicenseWatcher",

    [string] $StateRoot = "C:\ProgramData\SwLicenseWatcher",

    [switch] $KeepFiles,

    [switch] $RemoveState,

    [ValidateRange(1, 60)]
    [int] $PollSeconds = 3,

    [ValidateRange(1, 1440)]
    [int] $TimeoutMinutes = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workerServiceName = "SwLicenseWatcher.Agent.Worker"
$watchdogServiceName = "SwLicenseWatcher.Agent.Watchdog"

function Test-HasText {
    param($Value)
    return -not [string]::IsNullOrWhiteSpace([string] $Value)
}

function Get-JsonProperty {
    param(
        [Parameter(Mandatory)] $Object,
        [Parameter(Mandatory)] [string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }

    $pascal = $Name.Substring(0, 1).ToUpperInvariant() + $Name.Substring(1)
    $property = $Object.PSObject.Properties[$pascal]
    if ($null -ne $property) {
        return $property.Value
    }

    return $null
}

function Assert-ServerBaseUrl {
    param([Parameter(Mandatory)] [string] $Url)

    $uri = [Uri]::new($Url)
    if (-not $uri.IsAbsoluteUri) {
        throw "ServerBaseUrl must be an absolute URL."
    }

    $https = $uri.Scheme -eq [Uri]::UriSchemeHttps
    $httpLoopback = $uri.Scheme -eq [Uri]::UriSchemeHttp -and $uri.IsLoopback
    if (-not ($https -or $httpLoopback)) {
        throw "ServerBaseUrl must use HTTPS (HTTP is allowed only for loopback diagnostics)."
    }
}

function Invoke-AgentApi {
    param(
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $Path,
        $Body
    )

    $uri = $ServerBaseUrl.TrimEnd('/') + $Path
    $headers = @{ Authorization = "Bearer $ApiToken" }
    if ($null -ne $DeviceProof) {
        if (Test-HasText $DeviceProof.DeviceId) {
            $headers['X-Device-Id'] = $DeviceProof.DeviceId
        }

        $headers['X-Device-Proof'] = $DeviceProof.Proof
    }
    $params = @{
        Method      = $Method
        Uri         = $uri
        Headers     = $headers
        TimeoutSec  = 30
    }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json; charset=utf-8'
        $params.Body = ($Body | ConvertTo-Json -Compress)
    }

    return Invoke-RestMethod @params
}

function Remove-WindowsService {
    param([Parameter(Mandatory)] [string] $Name)

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        return
    }

    if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $Name -Force
        try {
            $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromMinutes(1))
        }
        catch {
            throw "Service $Name did not stop in time."
        }
    }

    & sc.exe delete $Name | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete $Name failed with exit code $LASTEXITCODE."
    }

    $deadline = [datetime]::UtcNow.AddSeconds(30)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($null -eq (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 300
    }
    throw "Service $Name was not deleted in time."
}

function Wait-UninstallGrant {
    param(
        [Parameter(Mandatory)] [long] $RequestId
    )

    $deadline = [datetime]::UtcNow.AddMinutes($TimeoutMinutes)
    $statusPath = "/api/agents/uninstall-requests/$RequestId" + '?deviceCode=' + [Uri]::EscapeDataString($DeviceCode)
    Write-Host "Waiting for administrator approval in /admin (request $RequestId). DeviceCode=$DeviceCode"
    while ([datetime]::UtcNow -lt $deadline) {
        $status = Invoke-AgentApi -Method GET -Path $statusPath
        $state = [string] (Get-JsonProperty -Object $status -Name 'status')
        if ($state -eq 'approved') {
            $code = [string] (Get-JsonProperty -Object $status -Name 'code')
            if (-not (Test-HasText $code)) {
                throw "The uninstall grant was approved but no code was returned."
            }

            Write-Host "Uninstall grant issued for $DeviceCode."
            return $code
        }

        if ($state -eq 'denied') {
            throw "The uninstall request was denied. Services were not removed."
        }

        if ($state -eq 'expired' -or $state -eq 'consumed') {
            throw "The uninstall grant is $state. Services were not removed."
        }

        Write-Host "Uninstall request is $state. Waiting for approval..."
        Start-Sleep -Seconds $PollSeconds
    }

    throw "Timed out waiting for uninstall approval after $TimeoutMinutes minute(s). Services were not removed."
}

if ([string]::IsNullOrWhiteSpace($ApiToken) -or $ApiToken.Length -lt 32) {
    throw "ApiToken must be at least 32 characters."
}
if ([string]::IsNullOrWhiteSpace($DeviceCode)) {
    throw "DeviceCode is required."
}

Assert-ServerBaseUrl -Url $ServerBaseUrl

function Get-InstalledDeviceProof {
    $workerExe = Join-Path $InstallRoot "Agent.Worker\SwLicenseWatcher.Agent.Worker.exe"
    if (-not (Test-Path -LiteralPath $workerExe)) {
        throw "Worker executable was not found at '$workerExe'. Device proof cannot be created."
    }

    $output = & $workerExe --print-device-proof "--device-code=$DeviceCode"
    if ($LASTEXITCODE -ne 0) {
        throw "The installed Worker could not create a device proof for $DeviceCode."
    }

    $lines = @($output | Where-Object { $_ -ne $null })
    if ($lines.Count -lt 2 -or -not (Test-HasText $lines[$lines.Count - 1])) {
        throw "The installed Worker did not return a device proof."
    }

    return @{
        DeviceId = [string] $lines[$lines.Count - 2]
        Proof    = [string] $lines[$lines.Count - 1]
    }
}

Write-Host "Requesting an uninstall grant from $ServerBaseUrl for $DeviceCode"
$DeviceProof = Get-InstalledDeviceProof
$created = Invoke-AgentApi -Method POST -Path '/api/agents/uninstall-requests' -Body @{
    deviceCode  = $DeviceCode
    deviceId    = $DeviceProof.DeviceId
    deviceProof = $DeviceProof.Proof
}
$requestId = [long] (Get-JsonProperty -Object $created -Name 'id')
if ($requestId -le 0) {
    throw "The API did not return an uninstall request id."
}

$code = Wait-UninstallGrant -RequestId $requestId
Invoke-AgentApi -Method POST -Path "/api/agents/uninstall-requests/$requestId/consume" -Body @{
    deviceCode  = $DeviceCode
    code        = $code
    deviceId    = $DeviceProof.DeviceId
    deviceProof = $DeviceProof.Proof
}
Write-Host "Uninstall grant consumed. Removing services."

Remove-WindowsService -Name $watchdogServiceName
Remove-WindowsService -Name $workerServiceName
Write-Host "Services $watchdogServiceName and $workerServiceName removed."

if (-not $KeepFiles) {
    foreach ($leaf in @("Agent.Watchdog", "Agent.Worker")) {
        $dir = Join-Path $InstallRoot $leaf
        if (Test-Path -LiteralPath $dir) {
            Remove-Item -LiteralPath $dir -Recurse -Force
            Write-Host "Removed $dir"
        }
    }

    $parentEmpty = (Test-Path -LiteralPath $InstallRoot) -and
        $null -eq (Get-ChildItem -LiteralPath $InstallRoot -Force | Select-Object -First 1)
    if ($parentEmpty) {
        Remove-Item -LiteralPath $InstallRoot -Force
    }
}

if ($RemoveState -and (Test-Path -LiteralPath $StateRoot)) {
    Remove-Item -LiteralPath $StateRoot -Recurse -Force
    Write-Host "Removed state directory $StateRoot"
}
elseif (-not $RemoveState) {
    Write-Host "Left $StateRoot in place. Pass -RemoveState to delete the queue, health file, staging, and backups."
}
