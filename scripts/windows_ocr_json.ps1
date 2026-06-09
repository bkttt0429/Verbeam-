param(
    [Parameter(Mandatory = $true)]
    [string]$Image,

    [string]$Language = "ja"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

Add-Type -AssemblyName System.Runtime.WindowsRuntime

[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
[Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null
[Windows.Foundation.IAsyncOperation`1, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null

$script:AsTask = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq "AsTask" -and
        $_.IsGenericMethodDefinition -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } |
    Select-Object -First 1

function Wait-WinRtOperation {
    param(
        [Parameter(Mandatory = $true)]
        $Operation,

        [Parameter(Mandatory = $true)]
        [Type]$ResultType
    )

    $task = $script:AsTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

function Convert-LanguageTag {
    param([string]$Value)

    switch -Regex ($Value.Trim().ToLowerInvariant()) {
        "^ja" { return "ja-JP" }
        "^jp" { return "ja-JP" }
        "^zh-tw" { return "zh-Hant" }
        "^zh-hant" { return "zh-Hant" }
        "^zh" { return "zh-Hans" }
        "^en" { return "en-US" }
        "^ko" { return "ko-KR" }
        default { return $Value }
    }
}

$resolvedImage = Resolve-Path -LiteralPath $Image
$languageTag = Convert-LanguageTag $Language
$languageObject = [Windows.Globalization.Language]::new($languageTag)
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($languageObject)

if ($null -eq $engine) {
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
}

if ($null -eq $engine) {
    throw "No Windows OCR engine is available for '$languageTag'. Install the OCR language pack in Windows Settings."
}

$file = Wait-WinRtOperation `
    -Operation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($resolvedImage.Path)) `
    -ResultType ([Windows.Storage.StorageFile])

$stream = Wait-WinRtOperation `
    -Operation ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) `
    -ResultType ([Windows.Storage.Streams.IRandomAccessStream])

try {
    $decoder = Wait-WinRtOperation `
        -Operation ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) `
        -ResultType ([Windows.Graphics.Imaging.BitmapDecoder])

    $bitmap = Wait-WinRtOperation `
        -Operation ($decoder.GetSoftwareBitmapAsync()) `
        -ResultType ([Windows.Graphics.Imaging.SoftwareBitmap])

    $result = Wait-WinRtOperation `
        -Operation ($engine.RecognizeAsync($bitmap)) `
        -ResultType ([Windows.Media.Ocr.OcrResult])

    $blocks = @()
    foreach ($line in $result.Lines) {
        $lineText = $line.Text
        $blocks += [ordered]@{
            text = $lineText
            confidence = 1.0
            boundingBox = $null
        }
    }

    [ordered]@{
        text = $result.Text
        blocks = $blocks
    } | ConvertTo-Json -Depth 6 -Compress
}
finally {
    if ($null -ne $stream) {
        $stream.Dispose()
    }
}
