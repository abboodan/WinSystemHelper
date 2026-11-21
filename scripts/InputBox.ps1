Add-Type -AssemblyName Microsoft.VisualBasic
$promptText = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{base64:prompt}}'))
$outputPath = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{base64:outputPath}}'))
$response = ''

while ([string]::IsNullOrWhiteSpace($response)) {
    $response = [Microsoft.VisualBasic.Interaction]::InputBox($promptText, 'WinSystemHelper', '')
}

[System.IO.File]::WriteAllText($outputPath, $response, [System.Text.Encoding]::UTF8)
exit 0
