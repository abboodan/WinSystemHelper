$ErrorActionPreference = 'Stop'

function Decode-Base64Utf8([string]$value) {
    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($value))
}

$ServiceName = Decode-Base64Utf8 '{{base64:serviceName}}'
$TargetProcessId = {{processId}}
$PayloadRoot = Decode-Base64Utf8 '{{base64:payloadRoot}}'
$TargetDirectory = Decode-Base64Utf8 '{{base64:targetDirectory}}'
$BackupDirectory = Decode-Base64Utf8 '{{base64:backupDirectory}}'
$PersistentBackupDirectory = Decode-Base64Utf8 '{{base64:persistentBackupDirectory}}'
$UpdateRoot = Decode-Base64Utf8 '{{base64:updateRoot}}'
$StatusMarkerPath = Decode-Base64Utf8 '{{base64:statusMarkerPath}}'
$LogPath = Decode-Base64Utf8 '{{base64:logPath}}'
$ConfigFileName = '{{configFileName}}'
$UpdateSucceeded = $false
$PathTrimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)

function Write-Log([string]$message) {
    $line = "$(Get-Date -Format o) $message"
    $logDirectory = [System.IO.Path]::GetDirectoryName($LogPath)
    if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }

    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Write-UpdateStatus([string]$message) {
    $statusDirectory = [System.IO.Path]::GetDirectoryName($StatusMarkerPath)
    if (-not [string]::IsNullOrWhiteSpace($statusDirectory)) {
        New-Item -ItemType Directory -Path $statusDirectory -Force | Out-Null
    }

    Set-Content -LiteralPath $StatusMarkerPath -Value $message -Encoding UTF8
}

function Invoke-Retry([scriptblock]$action, [string]$description) {
    $lastError = $null

    for ($attempt = 1; $attempt -le 30; $attempt++) {
        try {
            & $action
            return
        }
        catch {
            $lastError = $_
            Write-Log "$description failed on attempt ${attempt}: $($_.Exception.Message)"

            if ($attempt -lt 30) {
                Start-Sleep -Seconds 2
            }
        }
    }

    throw "Failed to complete '$description' after 30 attempts. Last error: $($lastError.Exception.Message)"
}

{{copyTree}}

function Wait-ForOriginalProcessExit {
    Start-Sleep -Seconds 3
    & sc.exe stop $ServiceName | Out-Null

    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
    }

    if (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue) {
        throw "Original service process $TargetProcessId did not exit before timeout."
    }
}

function Start-TargetService {
    $startOutput = & sc.exe start $ServiceName 2>&1
    $startExitCode = $LASTEXITCODE
    Write-Log "Service start requested. ExitCode=$startExitCode Output=$($startOutput -join ' ')"

    if ($startExitCode -ne 0 -and $startExitCode -ne 1056) {
        throw "sc.exe start failed with exit code $startExitCode. Output: $($startOutput -join ' ')"
    }

    $deadline = (Get-Date).AddSeconds(30)
    do {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -eq 'Running') {
            Write-Log "Service is running."
            return
        }

        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    throw "Service did not reach RUNNING state within 30 seconds after start."
}

try {
    Write-Log "OTA update script started."
    Wait-ForOriginalProcessExit

    if (Test-Path -LiteralPath $BackupDirectory) {
        Remove-Item -LiteralPath $BackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null

    Write-Log "Backing up current service files."
    Copy-Tree -source $TargetDirectory -destination $BackupDirectory

    Write-Log "Refreshing persistent rollback backup."
    if (Test-Path -LiteralPath $PersistentBackupDirectory) {
        Remove-Item -LiteralPath $PersistentBackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $PersistentBackupDirectory -Force | Out-Null
    Copy-Tree -source $BackupDirectory -destination $PersistentBackupDirectory

    Write-Log "Copying staged update files."
    Copy-Tree -source $PayloadRoot -destination $TargetDirectory

    Start-TargetService
    $UpdateSucceeded = $true
    Write-UpdateStatus "✅ OTA update applied successfully at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')."
    Write-Log "OTA update completed successfully."
}
catch {
    $failureMessage = $_.Exception.Message
    Write-Log "OTA update failed: $failureMessage"

    try {
        if (Test-Path -LiteralPath $BackupDirectory) {
            Write-Log "Attempting rollback from backup."
            Copy-Tree -source $BackupDirectory -destination $TargetDirectory
            Write-UpdateStatus "⚠️ OTA update failed and rollback was attempted at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'). Error: $failureMessage"
        }
        else {
            Write-UpdateStatus "⚠️ OTA update failed before backup was created at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'). Error: $failureMessage"
        }
    }
    catch {
        Write-UpdateStatus "🚨 OTA update failed and rollback also failed at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'). Update error: $failureMessage Rollback error: $($_.Exception.Message)"
        Write-Log "Rollback failed: $($_.Exception.Message)"
    }

    try {
        Start-TargetService
    }
    catch {
        Write-Log "Service restart failed after update failure: $($_.Exception.Message)"
        Write-UpdateStatus "🚨 OTA update failed and the service could not be restarted at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'). Error: $failureMessage Restart error: $($_.Exception.Message)"
    }
}
finally {
    try {
        if ($UpdateSucceeded -and (Test-Path -LiteralPath $UpdateRoot)) {
            Remove-Item -LiteralPath $UpdateRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Log "Failed to delete update staging directory: $($_.Exception.Message)"
    }

    $scriptPath = $MyInvocation.MyCommand.Path
    $escapedScriptPath = $scriptPath.Replace("'", "''")
    $cleanupScript = "Start-Sleep -Seconds 5; Remove-Item -LiteralPath '$escapedScriptPath' -Force -ErrorAction SilentlyContinue"
    $cleanupBytes = [System.Text.Encoding]::Unicode.GetBytes($cleanupScript)
    $cleanupEncoded = [System.Convert]::ToBase64String($cleanupBytes)
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $cleanupEncoded" -WindowStyle Hidden
}
