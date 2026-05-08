param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $RemainingArgs
)

$ErrorActionPreference = 'Stop'

$baseDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$appPath = Join-Path $baseDirectory 'WinSystemHelper.exe'
$logPath = Join-Path $baseDirectory 'WinSystemHelper.Bootstrapper.log'
$runtimeUrl = 'https://aka.ms/dotnet/8.0/dotnet-runtime-win-x64.exe'
$runtimeInstaller = Join-Path $env:TEMP 'dotnet-runtime-8-win-x64.exe'

function Write-BootstrapLog {
    param([string] $Message)
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'
    Add-Content -LiteralPath $logPath -Value "[$timestamp] $Message"
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-DotNet8Runtime {
    try {
        $runtimeOutput = & dotnet --list-runtimes 2>$null
        if ($runtimeOutput -match '^Microsoft\.NETCore\.App 8\.') {
            return $true
        }
    }
    catch {
    }

    $runtimeRoot = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
    if (Test-Path -LiteralPath $runtimeRoot) {
        $runtime = Get-ChildItem -LiteralPath $runtimeRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^8\.' } |
            Select-Object -First 1

        if ($null -ne $runtime) {
            return $true
        }
    }

    return $false
}

function Install-DotNet8Runtime {
    if (-not (Test-Administrator)) {
        throw 'Access denied. Run PowerShell as Administrator so the .NET Runtime can be installed.'
    }

    Write-Host '.NET 8 x64 Runtime is missing. Downloading Microsoft runtime installer...'
    Write-BootstrapLog "Downloading runtime from $runtimeUrl to $runtimeInstaller."

    Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeInstaller -UseBasicParsing -TimeoutSec 300

    Write-Host '.NET Runtime downloaded. Installing silently...'
    Write-BootstrapLog 'Starting runtime installer.'

    $process = Start-Process -FilePath $runtimeInstaller -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    Write-BootstrapLog "Runtime installer exited with code $($process.ExitCode)."

    if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
        throw ".NET Runtime installer failed with exit code $($process.ExitCode)."
    }
}

try {
    Write-BootstrapLog "install.ps1 started. Args: $($RemainingArgs -join ' ')"

    if (-not (Test-Path -LiteralPath $appPath)) {
        throw "WinSystemHelper.exe was not found beside install.ps1."
    }

    if (-not (Test-DotNet8Runtime)) {
        Install-DotNet8Runtime
    }

    if (-not (Test-DotNet8Runtime)) {
        throw '.NET 8 x64 Runtime installation completed, but the runtime was not detected.'
    }

    if (($RemainingArgs -contains '/install' -or $RemainingArgs -contains '/uninstall') -and -not (Test-Administrator)) {
        throw 'Access denied. Run PowerShell as Administrator to install or uninstall the service.'
    }

    $process = Start-Process -FilePath $appPath -ArgumentList $RemainingArgs -WorkingDirectory $baseDirectory -Wait -PassThru
    Write-BootstrapLog "WinSystemHelper exited with code $($process.ExitCode)."
    exit $process.ExitCode
}
catch {
    Write-BootstrapLog "Fatal error: $($_.Exception.Message)"
    Write-Error $_.Exception.Message
    exit 1
}
