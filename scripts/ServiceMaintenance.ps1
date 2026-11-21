$ErrorActionPreference = 'Stop'
function Decode-Base64Utf8([string]$value) { [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($value)) }
$Mode = Decode-Base64Utf8 '{{base64:mode}}'
$ServiceName = Decode-Base64Utf8 '{{base64:serviceName}}'
$TargetProcessId = {{processId}}
$ExecutablePath = Decode-Base64Utf8 '{{base64:executablePath}}'
$SourceDirectory = Decode-Base64Utf8 '{{base64:sourceDirectory}}'
$TargetDirectory = Decode-Base64Utf8 '{{base64:targetDirectory}}'
$StatusMarkerPath = Decode-Base64Utf8 '{{base64:statusMarkerPath}}'
$LogPath = Decode-Base64Utf8 '{{base64:logPath}}'
$ConfigFileName = '{{configFileName}}'
$PathTrimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)

function Write-Log([string]$message) {
    $logDirectory = [System.IO.Path]::GetDirectoryName($LogPath)
    if (-not [string]::IsNullOrWhiteSpace($logDirectory)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
    Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) $message" -Encoding UTF8
}

function Write-Status([string]$message) {
    $statusDirectory = [System.IO.Path]::GetDirectoryName($StatusMarkerPath)
    if (-not [string]::IsNullOrWhiteSpace($statusDirectory)) { New-Item -ItemType Directory -Path $statusDirectory -Force | Out-Null }
    Set-Content -LiteralPath $StatusMarkerPath -Value $message -Encoding UTF8
}

function Invoke-Sc([string[]]$arguments, [string]$description, [int[]]$allowedExitCodes = @(0)) {
    $output = & sc.exe @arguments 2>&1
    $exitCode = $LASTEXITCODE
    Write-Log "$description exit=$exitCode output=$($output -join ' ')"
    if ($allowedExitCodes -notcontains $exitCode) { throw "$description failed with exit code $exitCode. Output: $($output -join ' ')" }
}

function Invoke-Retry([scriptblock]$action, [string]$description) {
    $lastError = $null
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        try { & $action; return }
        catch {
            $lastError = $_
            Write-Log "$description failed on attempt ${attempt}: $($_.Exception.Message)"
            if ($attempt -lt 30) { Start-Sleep -Seconds 2 }
        }
    }
    throw "Failed to complete '$description'. Last error: $($lastError.Exception.Message)"
}

{{copyTree}}

function Wait-ForCurrentServiceProcessExit {
    Start-Sleep -Seconds 3
    Invoke-Sc -arguments @('stop', $ServiceName) -description 'sc stop' -allowedExitCodes @(0,1060,1062)
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
}

function Start-TargetService {
    Invoke-Sc -arguments @('start', $ServiceName) -description 'sc start' -allowedExitCodes @(0,1056)
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -eq 'Running') { return }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "Service did not reach RUNNING state."
}

try {
    Write-Log "Maintenance script started. Mode=$Mode"
    if ($Mode -eq 'reinstall') {
        Wait-ForCurrentServiceProcessExit
        Invoke-Sc -arguments @('delete', $ServiceName) -description 'sc delete' -allowedExitCodes @(0,1060)
        Start-Sleep -Seconds 3
        # New-Service stores the path exactly as given, so the quotes survive;
        # an unquoted path with spaces could start a different executable.
        New-Service -Name $ServiceName -BinaryPathName ('"' + $ExecutablePath + '"') -StartupType Automatic | Out-Null
        Write-Log "Service created with a quoted executable path."
        Invoke-Sc -arguments @('description', $ServiceName, 'Hybrid .NET 8 Windows Service for remote device management via Telegram.') -description 'sc description' -allowedExitCodes @(0)
        Start-TargetService
        Write-Status "✅ WinSystemHelper service reinstalled successfully at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')."
    }
    elseif ($Mode -eq 'rollback') {
        Wait-ForCurrentServiceProcessExit
        Copy-Tree -source $SourceDirectory -destination $TargetDirectory
        Start-TargetService
        Write-Status "✅ WinSystemHelper rollback completed successfully at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')."
    }
    else {
        Wait-ForCurrentServiceProcessExit
        Start-TargetService
        Write-Status "✅ WinSystemHelper service restarted successfully at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')."
    }
}
catch {
    Write-Log "Maintenance failed: $($_.Exception.Message)"
    Write-Status "⚠️ WinSystemHelper maintenance '$Mode' failed at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'). Error: $($_.Exception.Message)"
}
finally {
    $scriptPath = $MyInvocation.MyCommand.Path
    $escapedScriptPath = $scriptPath.Replace("'", "''")
    $cleanupScript = "Start-Sleep -Seconds 5; Remove-Item -LiteralPath '$escapedScriptPath' -Force -ErrorAction SilentlyContinue"
    $cleanupBytes = [System.Text.Encoding]::Unicode.GetBytes($cleanupScript)
    $cleanupEncoded = [System.Convert]::ToBase64String($cleanupBytes)
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $cleanupEncoded" -WindowStyle Hidden
}
