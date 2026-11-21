$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public sealed class CappedCaptureFile : System.IO.FileStream {
    private readonly long limit;
    public CappedCaptureFile(string path, long max) : base(path, System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite) { limit = max; }
    public override void Write(byte[] buffer, int offset, int count) {
        if (Position + count > limit) throw new System.IO.IOException("Image exceeds reserved space.");
        base.Write(buffer, offset, count);
    }
    public override void SetLength(long value) {
        if (value > limit) throw new System.IO.IOException("Image exceeds reserved space.");
        base.SetLength(value);
    }
}
public static class DpiNative {
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
}
'@

try {
    [DpiNative]::SetProcessDpiAwareness(2) | Out-Null
}
catch {
    [DpiNative]::SetProcessDPIAware() | Out-Null
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$outputPath = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{base64:outputPath}}'))
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$bounds = $screen.Bounds
$width = [DpiNative]::GetSystemMetrics(0)
$height = [DpiNative]::GetSystemMetrics(1)

if ($width -le 0 -or $height -le 0) {
    $width = $bounds.Width
    $height = $bounds.Height
}

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, (New-Object System.Drawing.Size $width, $height))
    $stream = New-Object CappedCaptureFile $outputPath, 10485760
    try { $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Jpeg); $stream.Flush($true) }
    finally { $stream.Dispose() }
    [IO.File]::WriteAllText($outputPath + '.complete','complete')
    exit 0
}
catch {
    exit 1
}
finally {
    if ($null -ne $graphics) {
        $graphics.Dispose()
    }

    if ($null -ne $bitmap) {
        $bitmap.Dispose()
    }
}
