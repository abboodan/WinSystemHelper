$ErrorActionPreference = 'Stop'

$outputPath = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{base64:outputPath}}'))

function Await-Action($asyncAction) {
    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.FullName -eq 'Windows.Foundation.IAsyncAction'
    } | Select-Object -First 1)

    if ($null -eq $asTask) {
        throw 'Unable to locate Windows Runtime AsTask(IAsyncAction).'
    }

    $task = $asTask.Invoke($null, @($asyncAction))
    $task.GetAwaiter().GetResult()
}

function Await-Operation($asyncOperation, [Type]$resultType) {
    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.IsGenericMethodDefinition -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1)

    if ($null -eq $asTask) {
        throw 'Unable to locate Windows Runtime AsTask(IAsyncOperation<T>).'
    }

    $typedAsTask = $asTask.MakeGenericMethod($resultType)
    $task = $typedAsTask.Invoke($null, @($asyncOperation))
    return $task.GetAwaiter().GetResult()
}

$failureCode = {{cameraInitializationFailedExitCode}}
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime

    [Windows.Media.Capture.MediaCapture, Windows.Media.Capture, ContentType = WindowsRuntime] | Out-Null
    [Windows.Media.Capture.MediaCaptureInitializationSettings, Windows.Media.Capture, ContentType = WindowsRuntime] | Out-Null
    [Windows.Media.Capture.StreamingCaptureMode, Windows.Media.Capture, ContentType = WindowsRuntime] | Out-Null
    [Windows.Media.MediaProperties.ImageEncodingProperties, Windows.Media.MediaProperties, ContentType = WindowsRuntime] | Out-Null

    $settings = New-Object Windows.Media.Capture.MediaCaptureInitializationSettings
    $settings.StreamingCaptureMode = [Windows.Media.Capture.StreamingCaptureMode]::Video

    $mediaCapture = New-Object Windows.Media.Capture.MediaCapture
    Await-Action ($mediaCapture.InitializeAsync($settings))

    $failureCode = {{cameraCaptureFailedExitCode}}
    [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
    $memory = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    $imageProperties = [Windows.Media.MediaProperties.ImageEncodingProperties]::CreateJpeg()
    $imageProperties.Width = 1920
    $imageProperties.Height = 1080
    Await-Action ($mediaCapture.CapturePhotoToStreamAsync($imageProperties, $memory))
    if ($memory.Size -le 0 -or $memory.Size -gt 10485760) { throw 'Image exceeds reserved capacity.' }
    $reader = [Windows.Storage.Streams.DataReader]::new($memory.GetInputStreamAt(0))
    Await-Operation ($reader.LoadAsync([uint32]$memory.Size)) ([uint32]) | Out-Null
    $bytes = New-Object byte[] ([int]$memory.Size)
    $reader.ReadBytes($bytes)
    $reader.Dispose()
    $memory.Dispose()

    $failureCode = {{cameraSaveFailedExitCode}}
    # The service already created the reservation. Its parent directories are private.
    $output = [IO.File]::Open($outputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $output.Write($bytes,0,$bytes.Length); $output.Flush($true) } finally { $output.Dispose() }
    [IO.File]::WriteAllText($outputPath + '.complete','complete')

    exit 0
}
catch {
    exit $failureCode
}
finally {
    if ($null -ne $mediaCapture -and $mediaCapture -is [System.IDisposable]) {
        $mediaCapture.Dispose()
    }
}
