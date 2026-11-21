$message = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{base64:message}}'))
(New-Object -ComObject SAPI.SpVoice).Speak($message) | Out-Null
exit 0
