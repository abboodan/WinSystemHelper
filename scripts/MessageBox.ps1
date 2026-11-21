Add-Type -AssemblyName System.Windows.Forms
$message = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{base64:message}}'))
$result = [System.Windows.Forms.MessageBox]::Show($message, 'WinSystemHelper', [System.Windows.Forms.MessageBoxButtons]::{{buttons}}, [System.Windows.Forms.MessageBoxIcon]::{{icon}})
