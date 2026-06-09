param(
    [string]$Model = "verbeam-mort-qwen2.5-0.5b:latest",
    [string]$Text = "",
    [int]$Iterations = 3,
    [int]$NumContext = 1024,
    [int]$NumPredict = 64
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:OLLAMA_HOST)) {
    $env:OLLAMA_HOST = "127.0.0.1:11434"
}

if ([string]::IsNullOrWhiteSpace($Text)) {
    $Text = -join ([char[]](
        0x3053, 0x3053, 0x306F, 0x5371, 0x967A, 0x3067,
        0x3059, 0x3002, 0x65E9, 0x304F, 0x9003, 0x3052,
        0x3066, 0x304F, 0x3060, 0x3055, 0x3044, 0x3002
    ))
}

$endpoint = "http://$($env:OLLAMA_HOST)/api/chat"
$system = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("5L2g5piv6YGK5oiy5Zyo5Zyw5YyW57+76K2v5Zmo44CC5oqK5pel5paH57+75oiQ5Y+w54Gj57mB6auU5Lit5paH44CC5Y+q6Ly45Ye66K2v5paH77yM5LiN6KaB6Kej6YeL77yM5LiN6KaB6Ly45Ye65pel5paH5Y6f5paH77yM5LiN6KaB5L2/55So57Ch6auU5Lit5paH44CC"))
$template = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("6KuL5oqK5LiL6Z2i5pel5paH57+75oiQ5Y+w54Gj57mB6auU5Lit5paH77yM5Y+q6Ly45Ye66K2v5paH44CCCgrooZPoqp7ooajvvJoKe0dMT1NTQVJZfQoK5pel5paH77yaCntURVhUfQoK57mB5Lit6K2v5paH77ya"))
$rows = New-Object System.Collections.Generic.List[object]

for ($i = 1; $i -le $Iterations; $i++) {
    $user = $template.Replace("{GLOSSARY}", "(none)").Replace("{TEXT}", $Text)

    $payload = @{
        model = $Model
        stream = $false
        keep_alive = "30m"
        options = @{
            temperature = 0
            num_ctx = $NumContext
            num_predict = $NumPredict
        }
        messages = @(
            @{ role = "system"; content = $system },
            @{ role = "user"; content = $user }
        )
    } | ConvertTo-Json -Depth 8

    $body = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $response = Invoke-RestMethod $endpoint -Method Post -ContentType "application/json; charset=utf-8" -Body $body

    $promptRate = 0
    if ($response.prompt_eval_duration -gt 0) {
        $promptRate = [math]::Round($response.prompt_eval_count / ($response.prompt_eval_duration / 1000000000), 2)
    }

    $evalRate = 0
    if ($response.eval_duration -gt 0) {
        $evalRate = [math]::Round($response.eval_count / ($response.eval_duration / 1000000000), 2)
    }

    $rows.Add([pscustomobject]@{
        iteration = $i
        total_ms = [math]::Round($response.total_duration / 1000000, 0)
        load_ms = [math]::Round($response.load_duration / 1000000, 0)
        prompt_tokens = $response.prompt_eval_count
        prompt_tps = $promptRate
        output_tokens = $response.eval_count
        output_tps = $evalRate
        response = ($response.message.content -replace '\r?\n', ' ').Trim()
    })
}

$rows | Format-Table -AutoSize
