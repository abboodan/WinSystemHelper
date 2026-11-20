$ErrorActionPreference = 'Stop'
$scanner = (Resolve-Path (Join-Path $PSScriptRoot '../scripts/Check-PublishSafety.ps1')).Path
$base = Join-Path ([IO.Path]::GetTempPath()) 'WinSystemHelper-publish-tests'
$root = Join-Path $base ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$original = Get-Location
function Check([bool]$ok, [string]$name) { if (-not $ok) { throw "FAIL: $name" }; Write-Host "PASS: $name" }
try {
    Set-Location -LiteralPath $root
    & git init -q
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize test repository.' }
    Set-Content -LiteralPath 'safe.md' -Value 'Synthetic test content.' -Encoding UTF8
    & git add safe.md
    $output = & powershell -NoProfile -File $scanner -Mode Index 2>&1
    Check ($LASTEXITCODE -eq 0) 'Clean index accepted'
    # Build a synthetic credential at runtime; no actual secret is used or printed.
    $synthetic = ('9' * 9) + ':' + ('Z' * 35)
    Set-Content -LiteralPath 'candidate.md' -Value $synthetic -Encoding UTF8
    & git add candidate.md
    $output = & powershell -NoProfile -File $scanner -Mode Index 2>&1
    Check ($LASTEXITCODE -eq 1) 'Credential-shaped staged content blocks publication'
    Check (-not (($output -join "`n").Contains($synthetic))) 'Credential value never appears in output'
    Set-Content -LiteralPath 'candidate.md' -Value 'Working tree cleaned, staged content remains private.' -Encoding UTF8
    $output = & powershell -NoProfile -File $scanner -Mode Index 2>&1
    Check ($LASTEXITCODE -eq 1) 'Scanner checks staged blob rather than working copy'
    & git rm --cached -f -q candidate.md
    Set-Content -LiteralPath 'config.json' -Value '{}' -Encoding UTF8
    & git add config.json
    $output = & powershell -NoProfile -File $scanner -Mode Index 2>&1
    Check ($LASTEXITCODE -eq 1) 'Tracked configuration blocks publication'
    & git rm --cached -q config.json
    $output = & powershell -NoProfile -File $scanner -Mode History -RevisionRange 'missing-ref' 2>&1
    Check ($LASTEXITCODE -eq 2) 'Inspection failure blocks publication'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    $package = Join-Path $root 'synthetic.zip'
    $zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    try { $zip.CreateEntry('private.json') | Out-Null } finally { $zip.Dispose() }
    $output = & powershell -NoProfile -File $scanner -Mode Index -Package $package 2>&1
    Check ($LASTEXITCODE -eq 2) 'Unexpected release entry blocks publication'
}
finally {
    Set-Location -LiteralPath $original.Path
    $safeBase = [IO.Path]::GetFullPath($base).TrimEnd('\') + '\'
    $safeRoot = [IO.Path]::GetFullPath($root)
    if ($safeRoot.StartsWith($safeBase, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $safeRoot -Recurse -Force
    }
}
