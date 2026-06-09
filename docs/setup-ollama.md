# Ollama Setup

Install and start Ollama, then pull a translation-capable model.

```powershell
ollama pull qwen2.5:0.5b
```

Create the low-latency MORT profile:

```powershell
.\scripts\create-ollama-profiles.ps1
```

After editing the Modelfile, recreate the profile:

```powershell
.\scripts\create-ollama-profiles.ps1 -Force
```

This creates:

```text
lth-mort-qwen2.5-0.5b:latest
```

The profile is based on `qwen2.5:0.5b` and sets a smaller context, short output cap, deterministic temperature, and a strict translation-only system prompt. The Modelfile is [ollama/Modelfile.mort-qwen2.5-0.5b](../ollama/Modelfile.mort-qwen2.5-0.5b).

Confirm the local API is available:

```powershell
Invoke-RestMethod http://localhost:11434/api/tags
```

LocalTranslateHub uses Ollama's native chat endpoint:

```text
POST http://localhost:11434/api/chat
```

Change model or base URL in:

```text
src/LocalTranslateHub.Api/appsettings.json
```

Useful settings:

```json
{
  "LocalTranslateHub": {
    "Ollama": {
      "BaseUrl": "http://localhost:11434",
      "Model": "lth-mort-qwen2.5-0.5b:latest",
      "TimeoutSeconds": 30,
      "NumContext": 1024,
      "NumPredict": 64,
      "Temperature": 0,
      "KeepAlive": "30m"
    }
  }
}
```

For best latency on NVIDIA GPUs, start Ollama with:

```powershell
$env:OLLAMA_FLASH_ATTENTION='1'
$env:OLLAMA_KEEP_ALIVE='30m'
```

The prepared `.\scripts\start-env.ps1` sets these environment variables when it starts the standalone Ollama server.

Benchmark the current model and stack:

```powershell
.\scripts\benchmark-ollama.ps1
```
