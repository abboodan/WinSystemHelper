param(
    [ValidateSet('WorkingTree','Index','History')][string]$Mode = 'WorkingTree',
    [string]$RevisionRange = '--all',
    [string]$Package
)
$ErrorActionPreference = 'Stop'
$findings = 0

function Invoke-GitText([string[]]$Arguments) {
    $result = @(& git @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Git inspection failed. Publication is blocked.' }
    return $result
}

function Test-Name([string]$Name) {
    return $Name -match '(?i)(^|/)(bin|obj|dist|artifacts|publish|build-verify[^/]*|logs|queue|capture|backups|\.vs)/' -or
        $Name -match '(?i)(^|/)(\.env($|\.)|config\.json($|\.)|appsettings(\.[^/]+)?\.json|secrets\.json)' -or
        $Name -match '(?i)\.(log|zip|7z|wav|mp3|ogg|m4a|mp4|jpg|jpeg|png|webp|db|db-wal|db-shm|sqlite|sqlite3|pem|pfx|p12|key|dump|dmp|tmp|temp|exe|dll|pdb)$'
}

function Test-Content([string]$Name, [string[]]$Lines) {
    $patterns = [ordered]@{
        'Telegram credential' = '[0-9]{8,}:[A-Za-z0-9_-]{20,}'
        'GitHub credential' = 'gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}'
        'Private key' = '-----BEGIN (RSA |OPENSSH |EC |DSA )?PRIVATE KEY-----'
        'Cloud access key' = 'AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}'
        'Personal machine path' = '[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b|Default\b)[A-Za-z0-9_. -]+'
        'Literal administrator id' = '(?i)(AdminChatIds?|chat_id)["\s]*[:=]\s*[\["\s]*-?[0-9]{6,}'
        'Credential in URL' = 'https?://[^\s/"<>]+:[^\s/"<>]+@'
    }
    for ($line = 0; $line -lt $Lines.Count; $line++) {
        foreach ($kind in $patterns.Keys) {
            if ($Lines[$line] -match $patterns[$kind]) {
                # Never print matching source text, credentials, or exception objects.
                Write-Host ('{0}:{1} [{2}]' -f $Name, ($line + 1), $kind)
                $script:findings++
            }
        }
    }
}

try {
    if ($Mode -eq 'History') {
        if ($RevisionRange -ne '--all' -and $RevisionRange.StartsWith('-')) { throw 'Invalid revision range.' }
        $commits = @(Invoke-GitText @('rev-list', $RevisionRange))
        foreach ($commit in $commits) {
            foreach ($name in @(Invoke-GitText @('ls-tree','-r','--name-only',$commit))) {
                if (Test-Name $name) { Write-Host "$commit`: $name [private/generated file]"; $findings++; continue }
                Test-Content "$commit`:$name" @(Invoke-GitText @('show', "$commit`:$name"))
            }
        }
    }
    else {
        $names = if ($Mode -eq 'Index') { @(Invoke-GitText @('ls-files')) }
            else { @(Invoke-GitText @('ls-files','--cached','--others','--exclude-standard') | Sort-Object -Unique) }
        foreach ($name in $names) {
            if ($Mode -eq 'WorkingTree' -and -not (Test-Path -LiteralPath $name)) { continue }
            if (Test-Name $name) { Write-Host "$name [private/generated file]"; $findings++; continue }
            $lines = if ($Mode -eq 'Index') { @(Invoke-GitText @('show', ":$name")) }
                else { @(Get-Content -LiteralPath $name -Encoding UTF8) }
            Test-Content $name $lines
        }
    }
    if ($Package) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
        try {
            $allowed = @('WinSystemHelper.exe','WinSystemHelper.Bootstrapper.exe','install.ps1','README.md')
            $names = @($zip.Entries | ForEach-Object { $_.FullName })
            if ($names.Count -ne $allowed.Count -or @($names | Select-Object -Unique).Count -ne $allowed.Count) {
                throw 'Release package contains unexpected or duplicate entries.'
            }
            foreach ($name in $names) { if ($name -cnotin $allowed) { throw 'Release package allowlist mismatch.' } }
            foreach ($entry in $zip.Entries) {
                if ($entry.Name -in @('install.ps1','README.md')) {
                    $reader = New-Object IO.StreamReader($entry.Open())
                    try { Test-Content "package:$($entry.FullName)" @($reader.ReadToEnd() -split "`n") }
                    finally { $reader.Dispose() }
                }
            }
        }
        finally { $zip.Dispose() }
    }
    if ($findings -gt 0) { Write-Host "Blocked: $findings potential private-content findings. Values were not displayed."; exit 1 }
    Write-Host "Publish safety checks passed ($Mode). Pattern checks are not a guarantee; review the final diff."
    exit 0
}
catch {
    Write-Host 'Safety inspection could not complete. Publication is blocked; no source content was printed.'
    exit 2
}
