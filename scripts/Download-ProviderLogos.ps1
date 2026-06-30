$ErrorActionPreference = "Continue"
$providers = @(
    "openai", "anthropic", "google", "gemini", "meta", "mistral", "groq",
    "cohere", "perplexity", "huggingface", "ai21", "together", "azure", "aws", "bedrock"
)

$baseUrl = "https://raw.githubusercontent.com/lobehub/lobe-icons/master/packages/static-svg/icons"
$targetDir = "d:\LocalTranslateHub\app\src\Verbeam.Api\wwwroot\images\providers"

if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir | Out-Null
}

Write-Host "Downloading provider logos to $targetDir..."

foreach ($p in $providers) {
    $colorUrl = "$baseUrl/$p-color.svg"
    $monoUrl = "$baseUrl/$p.svg"

    $colorFile = "$targetDir\$p-color.svg"
    $monoFile = "$targetDir\$p.svg"

    try {
        Invoke-WebRequest -Uri $colorUrl -OutFile $colorFile -UseBasicParsing -ErrorAction Stop
        Write-Host "Downloaded: $p-color.svg"
    } catch {
        Write-Host "Not found: $p-color.svg" -ForegroundColor Yellow
    }

    try {
        Invoke-WebRequest -Uri $monoUrl -OutFile $monoFile -UseBasicParsing -ErrorAction Stop
        Write-Host "Downloaded: $p.svg"
    } catch {
        Write-Host "Not found: $p.svg" -ForegroundColor Yellow
    }
}

Write-Host "Finished downloading logos!" -ForegroundColor Green
