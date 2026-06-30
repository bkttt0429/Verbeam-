# Web Translation Performance Plan

## Goal

Make browser page translation feel as fast as the local realtime path while
preserving quality, cache behavior, and provider flexibility.

This document is intentionally a design and measurement plan. It does not
change runtime behavior by itself.

## Non-Goals

- Do not raise all concurrency knobs blindly.
- Do not merge local model work into one large prompt unless a benchmark proves
  it is faster for the target model.
- Do not replace the existing DOM lazy translation pipeline.
- Do not optimize only llama.cpp; the final design must also help API-compatible
  providers.

## Current Baseline

Relevant files:

- `extensions/verbeam-web-translator/content-script.js`
- `extensions/verbeam-web-translator/background.js`
- `src/Verbeam.Core/Services/TranslationService.cs`
- `src/Verbeam.Core/Providers/LlamaCppTranslationProvider.cs`
- `src/Verbeam.Core/Providers/OllamaTranslationProvider.cs`
- `src/Verbeam.Core/Providers/ApiCompatibleTranslationProvider.cs`
- `src/Verbeam.Core/Services/TextChunker.cs`
- `src/Verbeam.Core/Services/DocumentJobService.cs`
- `models.catalog.json`

Observed design:

- Content script uses `IntersectionObserver` for visible-block lazy work.
- Content script uses `MutationObserver` for AJAX and SPA content.
- Content script has its own translation queue and browser-side exact cache.
- Background script batches only non-local providers.
- Local providers are excluded from extension-side batch merging.
- Backend translation has exact cache, glossary, prompt preset, provider call,
  cleanup, event write, and optional memory/context work.
- Long text can be split into chunks and translated concurrently inside one
  backend request.
- The local web profile already targets llama.cpp parallel slots.

## Working Hypothesis

The current slow path is less likely to be raw model token throughput and more
likely to be scheduling overhead and oversubscription.

Example failure mode:

```text
4 visible page blocks
* backend splits each long block into 4 chunks
= up to 16 provider calls racing for 4 local model slots
```

That can increase queue wait, reduce slot reuse, compete for prompt cache, and
make tail latency worse even if the model reports good token/s.

Local and API providers need different policies:

- Local llama.cpp/Ollama should keep independent work items flowing into the
  available slots, with bounded global backpressure.
- API-compatible providers should reduce request overhead through native batch
  requests, route/preset caching, and provider-specific rate limits.

## P0: Instrumentation First

Add measurement before changing behavior. The key objective is to separate
model time from queue time and fixed per-request overhead.

### Trace Identifiers

Every page translation item should carry:

- `traceId`: one browser page translation run.
- `itemId`: one DOM block or selected text.
- `chunkId`: optional backend text chunk.
- `provider`: resolved provider.
- `model`: resolved model.
- `mode`: web mode.
- `priority`: visible, prefetch, mutation, selection, node, or batch.

### Extension Metrics

Record these timings in `content-script.js` and `background.js`:

- block discovered time;
- block enqueued time;
- queue wait before request;
- background batch wait;
- fetch start and fetch end;
- render start and render end;
- cache hit or miss;
- retry count;
- abort or stale-session reason.

### Backend Metrics

Record these timings around the existing `/translate/web` path:

- request received;
- language detection start/end;
- exact memory/cache lookup start/end;
- glossary/preset load start/end;
- context/memory construction start/end;
- provider wait start/end;
- provider call start/end;
- output cleanup start/end;
- cache write start/end;
- event write start/end;
- total request wall time.

### Provider Metrics

For local providers:

- prompt character count;
- estimated prompt tokens if available;
- completion tokens;
- prompt eval time;
- decode time;
- tokens per second;
- llama slot id if reported by the runtime;
- cache prompt enabled;
- streaming or non-streaming.

For API-compatible providers:

- route lookup time;
- secret lookup time;
- request wall time;
- provider status code;
- input/output token counts if returned;
- retry and rate-limit status.

### Output Format

Use structured log lines or a small JSON trace file. Avoid only human-readable
strings because benchmark scripts need stable fields.

Minimum record shape:

```json
{
  "traceId": "page-...",
  "itemId": "block-...",
  "chunkId": "0",
  "provider": "llama-cpp",
  "model": "hy-mt2-1.8b-q4km",
  "stage": "provider.call",
  "startedAt": "2026-06-19T00:00:00.000Z",
  "durationMs": 1234,
  "promptChars": 812,
  "completionTokens": 118,
  "cacheHit": false
}
```

## P0 Benchmark Matrix

Run the same benchmark cases before and after every performance change.

### Synthetic Cases

- 20 tiny blocks, 40 to 80 chars each.
- 20 medium blocks, 250 to 450 chars each.
- 8 long blocks, 900 to 1600 chars each.
- 1 long article, 6000 to 10000 chars.
- Mixed page: title, nav noise, captions, paragraphs, and list items.

### Live Page Cases

- CNN article page.
- Documentation page with code blocks.
- Forum or comment-heavy page.
- AJAX/SPA page after route change.

### Reported Numbers

For each case report:

- time to first translated visible block;
- time to first 5 visible blocks;
- total page translation time;
- p50/p90/p95 per-block latency;
- queue wait versus provider time;
- tokens/s from provider;
- cache hit rate;
- failed block count;
- stale/canceled block count.

## P1: Provider-Aware Scheduler

Add a backend scheduling layer between request handling and provider calls.

### Resource Model

Each provider family exposes a capacity profile:

```text
provider: llama-cpp
kind: local
slots: 4
maxInFlight: 4
maxChunkFanoutPerRequest: dynamic
supportsBatchPrompt: false
supportsStreaming: true
```

```text
provider: openai-compatible
kind: api
maxInFlight: supplier-specific
maxBatchItems: supplier-specific
maxBatchChars: supplier-specific
supportsBatchPrompt: true
supportsStreaming: optional
```

### Local Provider Policy

For llama.cpp and Ollama:

- enforce one global limiter per provider/model endpoint;
- default capacity comes from profile parallelism;
- prefer runtime slot data if available;
- do not allow every request to fan out to full capacity;
- compute effective chunk fanout from current active requests;
- prioritize visible blocks over prefetch and mutation work;
- cancel stale queued work when the user restores the page or navigates away.

Suggested first formula:

```text
effectiveChunkFanout =
  clamp(1, configuredMaxChunkFanout, floor(slots / activePageRequests))
```

For example, with 4 slots:

- 1 active request can use up to 4 chunks.
- 2 active requests can use up to 2 chunks each.
- 4 active requests use 1 chunk each.

### API Provider Policy

For API-compatible providers:

- cache route, supplier, secret, and preset resolution for a short TTL;
- use native backend batch endpoint where possible;
- preserve per-item fallback if one batched item fails;
- expose supplier-specific rate and concurrency limits;
- prefer fewer larger requests until latency or truncation worsens.

### Priority Order

Default priority from highest to lowest:

1. Selection translation.
2. Node or hover-triggered translation.
3. Visible viewport blocks.
4. Near-viewport prefetch blocks.
5. Mutation/AJAX discovered blocks.
6. Background full-page remainder.

### Fairness

Avoid one page monopolizing all slots:

- limit in-flight items per browser page trace;
- rotate between page traces when multiple tabs are active;
- let user-triggered selection/node work jump ahead of passive page work.

## P2: Native Batch Endpoint

Add a backend endpoint after P0/P1 prove where the cost is.

Candidate route:

```text
POST /translate/web/batch
```

Request shape:

```json
{
  "sourceLanguage": "auto",
  "targetLanguage": "zh-TW",
  "provider": "default",
  "model": "default",
  "mode": "web_article",
  "contextMode": "fast",
  "items": [
    { "id": "block-1", "text": "..." },
    { "id": "block-2", "text": "..." }
  ]
}
```

Response shape:

```json
{
  "items": [
    {
      "id": "block-1",
      "translatedText": "...",
      "cacheHit": false,
      "latencyMs": 1234,
      "error": null
    }
  ],
  "trace": {
    "provider": "llama-cpp",
    "model": "hy-mt2-1.8b-q4km",
    "totalLatencyMs": 2345
  }
}
```

### Local Batch Behavior

For local providers, this endpoint should not simply concatenate all texts into
one prompt. Instead:

- perform per-item cache checks first;
- enqueue cache misses into the local scheduler;
- split long items by token budget;
- preserve item ordering in the response;
- return partial per-item errors instead of failing the whole batch.

### API Batch Behavior

For API providers, this endpoint can combine several items into one structured
prompt when the supplier supports it well:

- use JSON input/output, not delimiter-only parsing;
- validate that every input id has one output id;
- fallback failed or malformed items individually;
- tune `maxBatchItems` and `maxBatchChars` by provider.

## P3: Token-Aware Chunking

Current chunking is mostly character-budget based. That is simple, but it can
misestimate English, Chinese, code-heavy text, and mixed pages.

Plan:

- add an approximate token estimator for quick routing;
- optionally use llama.cpp `/tokenize` for calibration benchmarks;
- choose chunk size from token budget, not only characters;
- keep semantic boundaries where possible;
- tune completion max tokens together with chunk size.

Benchmark matrix:

```text
chunk target: 350, 500, 650, 800, 1000 chars
max output:   128, 160, 192, 256 tokens
slots:        2, 3, 4 where VRAM allows
```

Acceptance checks:

- no frequent truncation;
- stable translation quality;
- better p95 latency;
- no loss in total throughput.

## P4: DOM Backpressure

Keep the current lazy DOM approach but make it adaptive.

Ideas:

- shrink viewport prefetch margin when backend queue is saturated;
- expand prefetch margin when backend is idle;
- pause mutation rescans while too many items are queued;
- coalesce mutation roots before rescanning;
- skip boilerplate blocks earlier through article/main scoring;
- keep code/pre/navigation filters strict.

This should happen after backend queue telemetry exists. Otherwise the content
script cannot know whether it should feed more work or slow down.

## P5: Event And Cache Overhead

The cache front is already useful, but high-volume page translation can still
pay fixed SQLite/event costs.

Potential optimizations:

- batch translation event writes for non-realtime web translation;
- record compact per-item traces during page translation;
- flush detailed traces only when debug mode is enabled;
- avoid repeated preset/glossary/context work for items sharing the same trace.

## Acceptance Criteria

Do not call the optimization successful unless the benchmark report shows:

- lower time to first visible translation;
- lower p90 or p95 per-block latency;
- equal or better total page completion time;
- no increase in failed blocks;
- no obvious quality regression on long paragraphs;
- provider token/s is close to runtime expectation when local slots are full;
- queue wait is bounded and explainable.

## Implementation Order

1. Add P0 trace IDs and timings.
2. Add benchmark script and baseline report.
3. Add provider-aware backend scheduler behind a config flag.
4. Re-run benchmark against local llama.cpp and one API-compatible provider.
5. Add `/translate/web/batch` using scheduler and cache-first behavior.
6. Tune provider-specific batch and chunk defaults.
7. Add adaptive DOM backpressure from backend queue status.

## Research Anchors

- llama.cpp server supports parallel decoding, continuous batching, slots, and
  prompt-cache-related controls.
- Browser content scripts can read and mutate page DOM, while communicating
  with the extension worker.
- `IntersectionObserver` is the right base for lazy visible block translation.
- `MutationObserver` is the right base for AJAX/SPA changes, but needs
  coalescing and backpressure on busy pages.
- OpenAI-style offline batch APIs are useful for delayed document jobs, not for
  interactive page translation.
