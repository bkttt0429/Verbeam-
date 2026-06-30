# Verbeam Market And Machine Translation Notes

This document records the current product framing, market signals, competitor
discussion, and the working list of machine translation problems that Verbeam
should solve or explicitly acknowledge.

## Current Product Framing

Verbeam / LocalTranslateHub should not be positioned as a generic translation
app. The stronger framing is:

> Verbeam is a local-first translation hub for real-time game communication,
> screen/OCR translation, browser translation, document translation, subtitles,
> and speech translation, with glossary, cache, and RAG memory for consistent
> terminology and context.

The current project already points in this direction:

- `POST /translate` provides a MORT-compatible translation gateway.
- `POST /translate/web` supports browser/webpage translation workflows.
- OCR endpoints support image, document, and OCR-to-translation pipelines.
- ASR endpoints support audio, YouTube, live PCM, and ASR-to-translation
  pipelines.
- `/viewer`, `/projector`, and `/broadcast` provide subtitle/translation
  display surfaces.
- SQLite cache, glossary, prompt presets, translation events, and memory/RAG
  services support long-term translation consistency.
- Document jobs support text, markdown, html, pdf/image-style OCR, and Office
  style artifacts such as docx, pptx, and xlsx.

## Product Areas

### 1. Game Chat Real-Time Translation

This should be treated as the first sharp wedge.

The target workflow:

1. Detect foreign-language game chat from the screen or chat region.
2. Identify the other player's language.
3. Translate incoming chat into the user's language.
4. Let the user type a reply in their own language.
5. Translate the reply into the other player's language.
6. Copy, paste, or inject the translated reply into the game chat input.
7. Keep terminology and player-specific context consistent across the session.

This is different from normal OCR translation because it is interactive and
bidirectional. The user is not only reading foreign text; they are trying to
participate in a live conversation.

### 2. Screen And OCR Translation

This covers games, visual novels, manga, screenshots, app windows, and any
screen region where text must be extracted before translation.

The main product value is not simply "OCR plus translation." The value is
stability:

- reduce flicker from repeated OCR changes,
- avoid translating the same noisy text again and again,
- preserve layout when possible,
- merge or split OCR blocks correctly,
- remember corrections to OCR mistakes,
- translate consistently even when OCR output varies slightly frame to frame.

### 3. Document And Design File Translation

This includes design documents, technical specs, PDFs, markdown, HTML, Office
files, and product documents.

The key value is preserving meaning, structure, and terminology:

- translate long documents in chunks without losing context,
- keep product terms, UI labels, character names, and design terms consistent,
- avoid translating code, placeholders, formulas, table structure, and IDs,
- preserve enough layout or structured output to review and export.

### 4. Browser Translation

The browser extension should not compete only as a generic webpage translator.
Its strategic role is to connect web reading with the same Verbeam memory,
glossary, provider routing, and translation profiles used by games and
documents.

### 5. ASR, Subtitles, And Video Translation

This area overlaps with products like Ray, Whisper workflows, TurboScribe,
Subtitle Edit, and media-server subtitle tools.

Verbeam's useful angle is not "watch anything, understand everything." A better
claim is narrower:

- local-first audio and subtitle translation,
- controllable provider routing,
- exportable text/subtitle artifacts,
- terminology and context reuse across game, web, and document workflows.

## Market Signals From Ray Discussion

The Ray Kickstarter and surrounding discussions show that demand exists for
subtitle generation and translation, but users are cautious.

Observed concerns:

- The product scope is very large: subtitles, translation, dubbing,
  remastering, audio separation, IPTV, AI TV, API, media servers, and many
  platforms.
- Users ask whether the product is real or mostly AI hype.
- Users want clear integration details for Plex, Kodi, Jellyfin, Emby, and
  other existing media systems.
- Local vs cloud boundaries are confusing: local is private but hardware-bound;
  cloud is powerful but expensive and trust-dependent.
- Real-time transcription and translation may require hardware many users do
  not have.
- Lifetime cloud pricing creates sustainability questions because AI compute
  has ongoing cost.
- Machine translation quality is still questioned, especially when context,
  speaker identity, or scene meaning matters.

Useful references:

- Ray official site: https://rayplayer.com/en
- Ray FAQ: https://blog.rayplayer.com/ray-faq-everything-you-need-to-know/
- OSMC discussion: https://discourse.osmc.tv/t/ray-opensubtitles-ai-player/112602
- Reddit Plex discussion: https://www.reddit.com/r/PleX/comments/1rvfnj9/opensubtitles_just_announced_ray_could_plex_have/
- Reddit Addons4Kodi discussion: https://www.reddit.com/r/Addons4Kodi/comments/1rx8l31/ray_an_ai_media_player_by_opensubtitles/

Kickstarter comments were not fully accessible during research because the
comments page returned HTTP 403 from automated requests. The notes above are
based on public pages and related discussions.

## Current Machine Translation Problems

### Context Loss

Most machine translation systems handle a sentence or short segment at a time.
This causes wrong translations when meaning depends on earlier dialogue,
speaker identity, game state, UI context, or document section.

Examples:

- pronouns and omitted subjects,
- role or speaker confusion,
- ambiguous names,
- game skill names versus ordinary words,
- UI command text versus narrative text.

### Terminology Inconsistency

The same source term may be translated differently across time, providers, or
chunks. This is especially damaging in games, manga, technical docs, design
docs, and product specs.

Examples:

- character names,
- factions and locations,
- ability names,
- UI labels,
- product feature names,
- technical terms.

### OCR Noise

OCR introduces mistakes before translation starts. The translator may then
faithfully translate bad input.

Common causes:

- low contrast,
- stylized fonts,
- small subtitles,
- partial text capture,
- repeated frame-by-frame OCR,
- vertical or mixed-language text,
- overlapping UI elements,
- tables and complex document layout.

### Latency

Real-time use cases are sensitive to delay. A high-quality translation that
arrives too late may be useless in game chat, live subtitles, streams, or
overlay workflows.

Latency comes from:

- screen capture,
- OCR,
- language detection,
- prompt construction,
- model inference,
- network calls,
- rendering or input injection.

### Hallucination And Over-Inference

LLMs can add information that was not in the source text, especially when asked
to infer missing context. This is dangerous for subtitles, design documents,
technical documents, and game chat.

### Tone And Register

Literal translation may preserve words but lose tone. Chat messages, game
banter, fantasy dialogue, business specs, and UI copy require different styles.

### Mixed Language And Code Switching

Game chat and online communities often mix languages, abbreviations, slang,
emoji, romanization, and domain-specific terms. Language detection can fail on
short messages.

### Formatting And Structure

Documents, subtitles, UI text, tables, formulas, markdown, placeholders, and
code blocks should not all be translated the same way. Structure preservation is
part of translation quality.

### Privacy And Trust

Users may not want to upload game chat, screenshots, private documents, videos,
or internal design specs to a cloud provider. At the same time, local models may
not be fast or accurate enough for every task.

## Verbeam Solutions To Prioritize

### Translation Memory And RAG

Use glossary, trusted corrections, recent translation events, scene summaries,
and semantic retrieval to build compact context before translation.

The goal is not to dump huge context into every prompt. The goal is to pass only
the small pieces that improve this translation:

- known names,
- previous translations,
- scene or document summary,
- recent chat turns,
- project terminology,
- user-approved corrections.

### Provider Routing

Use different models for different workloads:

- fast local model for game chat and realtime overlay,
- stronger local or remote model for documents,
- cloud fallback for difficult text,
- OCR-specific provider routing for screenshot versus document/table/formula.

### Cache And Stabilization

Use exact and fuzzy cache for repeated or slightly changing OCR text. Pair this
with frame diff and OCR stabilizers so the same visual text does not create a
new translation every frame.

### User Correction Loop

Corrections should become durable memory:

- "this name should always be translated as X",
- "this OCR mistake should be fixed as Y",
- "this term should stay untranslated",
- "this style should be casual/formal/game-dialogue."

### Workload Profiles

Do not use one prompt for every scenario. Maintain profiles such as:

- game_chat,
- visual_novel_dialogue,
- manga,
- subtitle,
- web_article,
- design_document,
- technical_document,
- UI_copy.

### Bidirectional Chat Flow

For game chat, build a dedicated flow instead of treating it as generic OCR:

- detect incoming message language,
- track participants if possible,
- store short session context,
- translate incoming text,
- translate outgoing reply into the target participant language,
- preserve slang and brevity,
- expose copy/paste or input helper UX.

### Confidence And Fallbacks

Expose uncertainty rather than hiding it:

- OCR confidence low,
- language detection uncertain,
- translation provider failed,
- response likely too slow,
- glossary conflict found,
- remote provider unavailable.

Then fall back gracefully:

- cached translation,
- local model,
- remote model,
- raw OCR text,
- ask user to confirm correction.

## Problems Verbeam Cannot Fully Solve Yet

### Perfect Translation Is Not Achievable

No model can guarantee human-level translation for all languages, genres,
slang, jokes, cultural references, and ambiguous context.

Verbeam should promise controllability and consistency, not perfection.

### Bad Input Can Still Produce Bad Output

If OCR is wrong, audio is noisy, text is partial, or the game UI is hidden,
translation quality will be limited.

The product can reduce this with OCR routing, preprocessing, correction memory,
and confidence reporting, but it cannot fully remove the problem.

### Real-Time Local AI Depends On Hardware

Low-spec machines may not support high-quality OCR, ASR, and LLM translation in
real time. Cloud can help, but creates privacy, cost, and reliability tradeoffs.

### Game Input Injection May Be Risky

Automatically inserting translated replies into games may conflict with
anti-cheat systems, game terms of service, protected input fields, or platform
security policies.

Safer MVP options:

- copy translated reply to clipboard,
- show overlay with a copy button,
- provide an input helper outside the game,
- avoid keyboard/memory injection until risks are understood.

### Some Platforms Are Not Technically Accessible

Protected streaming apps, consoles, smart TVs, anti-capture surfaces, DRM video,
and sandboxed environments may block screen/audio capture or automation.

### Cloud Economics Are Not Free

If Verbeam offers cloud translation, ASR, OCR, or VLM fallback, pricing must
reflect real compute cost. Avoid vague lifetime cloud promises.

### Legal And Content Rights Issues

Subtitle extraction, hardcoded subtitle removal, dubbing, and media processing
can create copyright and platform-policy issues. Verbeam should stay focused on
user-owned content, accessibility, personal translation, and local workflows.

### Low-Resource Languages Remain Hard

Claims like "200+ languages" are easy to market but hard to guarantee. Quality
will vary significantly by language pair, domain, script, and available model
support.

## Research Addendum: MT Problems, Mitigations, And Hard Limits

Research date: 2026-06-13.

This section summarizes recent external research and standards that should guide
Verbeam's translation design.

### 1. MT Is Better, But Not Solved

The WMT 2024 General MT Shared Task is explicitly titled "The LLM Era Is Here
but MT Is Not Solved Yet." WMT 2025 continues evaluating broad MT quality across
30 language pairs. This supports a conservative product message: Verbeam should
claim better workflow control, not perfect translation.

Product implication:

- Do not market "perfect translation."
- Market "consistent, controllable, local-first translation workflows."
- Show confidence, provider, glossary hits, memory hits, and fallback path.

Sources:

- https://aclanthology.org/2024.wmt-1.1/
- https://steinst.is/files/2025_wmt_sharedtask.pdf

### 2. Terminology Is A First-Class Problem

The WMT 2025 Terminology Translation Task focuses on whether MT systems can use
extra terminology information and translate specialized terms accurately and
consistently, including high-stakes domains and document-level inputs.

Product implication:

- Verbeam's glossary and correction memory should be first-class UI, not hidden
  advanced settings.
- Each translation should record which glossary terms were applied.
- When a glossary conflict exists, the UI should mark it as a conflict rather
  than silently choosing one.
- For game chat and design documents, terminology consistency is a stronger
  value proposition than raw BLEU/COMET quality.

Sources:

- https://www2.statmt.org/wmt25/terminology.html
- https://aclanthology.org/2025.wmt-1.30/

### 3. Hallucination Can Be Reduced, Not Eliminated

Recent work on hallucination-focused preference optimization reports large
reductions in hallucinated translations after fine-tuning. This is encouraging,
but it is a model-training result, not a guarantee that arbitrary production
models will never hallucinate.

Product implication:

- Use constrained prompts and low-temperature decoding for translation.
- Run output checks for addition, omission, and unsupported detail.
- Re-translate or downgrade confidence when the output diverges from the source.
- Do not ask the model to "explain missing context" inside normal translation.
- Keep creative adaptation as a separate mode, not the default translation mode.

Sources:

- https://aclanthology.org/2025.naacl-long.175/
- https://direct.mit.edu/tacl/article/doi/10.1162/tacl_a_00615/118716/Hallucinations-in-Large-Multilingual-Translation

### 4. Document-Level Translation Needs Memory And Evaluation

Document-level MT research emphasizes discourse coherence, coreference,
lexical consistency, long-range dependencies, and evaluation gaps. Sentence-by-
sentence translation is easier, but it loses document meaning.

Product implication:

- For documents, translate with a document plan: outline, terminology table,
  chunk map, and section summaries.
- Use compact retrieval memory per chunk rather than dumping the whole document.
- Preserve placeholders, code blocks, IDs, formulas, table structure, and UI
  tokens.
- Keep human review loops for important documents.

Sources:

- https://www.cfilt.iitb.ac.in/resources/surveys/2025/survey_himanshu_document_level_machine_translation.pdf
- https://arxiv.org/html/2606.03078v1

### 5. OCR And ASR Errors Cascade Into Translation

OCR errors affect downstream MT. Speech translation pipelines also suffer from
ASR error propagation, especially in code-switching or noisy speech. For Verbeam,
this matters because game translation, screen translation, documents, and ASR
all have upstream recognition before MT.

Product implication:

- Treat OCR/ASR confidence as part of translation confidence.
- Store raw OCR/ASR text, corrected text, and final translation separately.
- Add an OCR correction memory before translation memory.
- Use repeated-frame stabilization before translation in realtime OCR.
- For code-switched chat or speech, avoid assuming one language per segment.

Sources:

- https://aclanthology.org/2022.findings-acl.92/
- https://arxiv.org/html/2406.10993v1
- https://aclanthology.org/2025.iwslt-1.41.pdf

### 6. Evaluation Metrics Are Useful But Not Enough

WMT24 Metrics shows fine-tuned neural metrics remain useful, and COMET/XCOMET
can score translations and detect errors. MQM provides a practical error
typology including accuracy, addition, mistranslation, omission, and MT
hallucination. But metrics still need diverse language/domain tests and should
not replace human review for high-risk content.

Product implication:

- Use reference-free QE as a warning signal, not an absolute truth.
- Adopt MQM-style error labels in internal diagnostics:
  - addition,
  - omission,
  - mistranslation,
  - terminology,
  - fluency,
  - locale convention,
  - style/register,
  - hallucination.
- Build a small Verbeam benchmark set from real game chat, OCR screenshots,
  design docs, subtitles, and web pages.

Sources:

- https://aclanthology.org/2024.wmt-1.2.pdf
- https://github.com/Unbabel/COMET
- https://themqm.org/error-types-2/typology/

### Practical Solution Matrix

| Problem | What Verbeam Can Do | What Remains Hard |
| --- | --- | --- |
| Terminology inconsistency | Glossary, translation memory, conflict UI, per-profile terms | New terms with no user-approved translation |
| Context loss | RAG memory, recent events, scene summaries, document chunk summaries | Long-range plot or implicit cultural context |
| OCR noise | OCR routing, preprocessing, stabilization, correction memory | Very stylized, tiny, overlapping, or animated text |
| ASR noise | captions-first strategy, chunking, confidence, glossary injection | Noisy audio, code-switching, overlapping speakers |
| Hallucination | constrained prompts, low temperature, QE checks, retranslation | Cannot guarantee zero hallucination |
| Latency | local fast model, cache, batching, async pipeline, fallback | High-quality local translation on weak hardware |
| Style/tone | workload profiles and examples | Humor, sarcasm, roleplay nuance, cultural references |
| Documents | chunk map, structure preservation, glossary, review artifacts | Complex PDFs, formulas, nested tables, diagrams |
| Privacy | local-first default, explicit cloud routes | Cloud quality requires sending data out |
| Evaluation | COMET/QE/MQM diagnostics, benchmark sets | Automatic metrics can miss subtle meaning errors |

### What Verbeam Should Explicitly Not Promise

- Perfect translation.
- Human-level cultural adaptation in every language.
- Zero hallucination.
- Real-time high-quality local ASR/OCR/MT on all hardware.
- Perfect layout preservation for arbitrary PDFs and design documents.
- Safe automatic text injection into every game.
- Uniform quality across "200+ languages."

## Positioning Guidance

Avoid this:

> Translate anything perfectly, instantly, everywhere.

Prefer this:

> Local-first translation workflows for games, chat, documents, and subtitles,
> with memory and terminology control.

For the first public wedge:

> Understand and reply to foreign-language game chat in real time, while keeping
> names, terms, and context consistent on your own machine.

## Next Discussion Questions

- Which exact game-chat flow should be MVP: OCR-only, clipboard-based, or input
  helper?
- Should Verbeam prioritize game chat before screen overlay translation, or ship
  both together?
- Which workload profiles need first-class prompts first?
- What should be local-only, and what can optionally use cloud providers?
- How should correction memory be reviewed, trusted, edited, and exported?
- What latency budget is acceptable for each use case?
- What problems should the UI explicitly mark as "not supported" in v1?
