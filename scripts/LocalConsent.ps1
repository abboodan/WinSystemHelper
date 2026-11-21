$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$dir = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{base64:directory}}'))
{{formSetup}}
$form.Text = 'WinSystemHelper - {{title}}'
$form.Size = New-Object Drawing.Size(510,235)
$form.TopMost = $true
$form.StartPosition = 'CenterScreen'
$label = New-Object Windows.Forms.Label
$label.Text = "{{prompt}}"
$label.SetBounds(15,15,465,115)
$start = New-Object Windows.Forms.Button
$start.Text = '{{allow}}'
$start.SetBounds(20,140,160,35)
$stop = New-Object Windows.Forms.Button
$stop.Text = 'Stop / Decline'
$stop.SetBounds(280,140,160,35)
$start.Add_Click({
    [IO.File]::WriteAllText((Join-Path $dir 'allow'),'yes')
    $start.Enabled = $false
    $stop.Text = '{{stopLabel}}'
    $label.Text = "{{active}}"
})
$stop.Add_Click({ $form.Close() })
$form.Add_FormClosing({ [IO.File]::WriteAllText((Join-Path $dir 'stop'),'stop') })
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 1000
$deadline = [DateTime]::UtcNow.AddSeconds(60)
$timer.Add_Tick({
    if ([IO.File]::Exists((Join-Path $dir 'done')) -or ($start.Enabled -and [DateTime]::UtcNow -gt $deadline)) { $form.Close() }
})
$form.Controls.AddRange(@($label,$start,$stop))
$timer.Start()
[Windows.Forms.Application]::Run($form)
$timer.Dispose()
$form.Dispose()
