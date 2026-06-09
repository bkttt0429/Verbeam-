# Model Recommendations

Start with a small local model for smoke tests:

- `lth-mort-qwen2.5-0.5b:latest`, created from `qwen2.5:0.5b` with the MORT low-latency Modelfile

For better translation quality, try a larger translation-focused local model if available:

- `translategemma:latest`
- Qwen or Gemma instruction models that perform well for Japanese to Chinese translation

For MORT overlays, prefer models that are:

- Fast enough for short OCR snippets
- Good at preserving names and terminology
- Able to follow "output only translated text"

On 4GB VRAM, prefer models that fully fit in VRAM before chasing larger model names. Start with sub-2B models and keep `num_ctx` low for real-time overlays.

Use `provider: "mock"` for tests when Ollama is not running.

Future DeepSeek or cloud-provider routing is tracked as roadmap work, not current behavior. See [產品路線：即時螢幕語境翻譯器](product-roadmap.md).
