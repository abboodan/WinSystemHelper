function Copy-Tree([string]$source, [string]$destination) {
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Source path does not exist: $source"
    }

    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    Get-ChildItem -LiteralPath $source -Recurse -Directory -Force | ForEach-Object {
        $relative = $_.FullName.Substring($source.Length).TrimStart($PathTrimChars)
        if (-not [string]::IsNullOrWhiteSpace($relative)) {
            New-Item -ItemType Directory -Path (Join-Path $destination $relative) -Force | Out-Null
        }
    }

    Get-ChildItem -LiteralPath $source -Recurse -File -Force | Where-Object {
        -not $_.Name.Equals($ConfigFileName, [System.StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object {
        $file = $_
        $relative = $file.FullName.Substring($source.Length).TrimStart($PathTrimChars)
        $targetPath = Join-Path $destination $relative
        $targetParent = [System.IO.Path]::GetDirectoryName($targetPath)

        if (-not [string]::IsNullOrWhiteSpace($targetParent)) {
            New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
        }

        Invoke-Retry { Copy-Item -LiteralPath $file.FullName -Destination $targetPath -Force } "copy $relative"
    }
}
