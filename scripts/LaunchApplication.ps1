$ErrorActionPreference = 'Stop'
$filePath = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{base64:filePath}}'))
$arguments = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{base64:arguments}}'))
$startInfo = @{
    FilePath = $filePath
}

try {
    $workingDirectory = [System.IO.Path]::GetDirectoryName($filePath)
    if (-not [string]::IsNullOrWhiteSpace($workingDirectory) -and (Test-Path -LiteralPath $workingDirectory)) {
        $startInfo.WorkingDirectory = $workingDirectory
    }
}
catch {
}

if (-not [string]::IsNullOrWhiteSpace($arguments)) {
    $startInfo.ArgumentList = $arguments
}

Start-Process @startInfo
exit 0
