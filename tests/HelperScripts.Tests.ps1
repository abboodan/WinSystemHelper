param([Parameter(Mandatory=$true)][string]$Folder)
$ErrorActionPreference = 'Stop'
foreach ($file in Get-ChildItem -LiteralPath $Folder -Filter '*.ps1') {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "Generated helper has a syntax error: $($file.Name)" }
    if ($file.Name -eq 'screenshot.ps1') {
        $definition = $ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
                $node.Value.Contains('public sealed class CappedCaptureFile')
        }, $true) | Select-Object -First 1
        if ($null -eq $definition) { throw 'Missing bounded screenshot stream.' }
        Add-Type -TypeDefinition $definition.Value
        $stream = New-Object CappedCaptureFile (Join-Path $Folder 'bounded.bin'), 10
        try {
            $rejected = $false
            try { $stream.Write((New-Object byte[] 11), 0, 11) } catch { $rejected = $true }
            if (-not $rejected -or $stream.Length -gt 10) { throw 'Bounded stream overflow.' }
        }
        finally { $stream.Dispose() }
    }
}
Write-Host 'Generated helper syntax and bounded stream verified; no screen/camera/audio capture executed.'
