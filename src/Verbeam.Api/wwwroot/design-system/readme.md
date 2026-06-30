# Verbeam — Design System

Verbeam is a **local-first universal translation desktop app**. It runs entirely on
the user's machine (no cloud round-trip by default) and translates across modalities:
typed text, OCR'd images, screen regions, and live audio. The default workload is
Japanese → Traditional Chinese (`ja → zh-TW`) for game text, subtitles and live
events, but any pair is supported.

The product wears an **"OpenCode desktop"** skin — a dark, monospace-accented technical
workbench, deliberately tool-like rather than consumer-glossy. The internal/codebase
name is **LocalTranslateHub** (you'll see the `LTH` rail badge and an `OC` brand mark,
a lineage nod); the user-facing brand is **Verbeam**.

## Surfaces / products

| Surface | Route | What it is |
|---|---|---|
| **Workbench** | `/app` | The main app. A 3-pane shell (activity rail · workspace sidebar · terminal + inspector) with modes for Translate, OCR, OCR+Translate, Audio, Audio+Translate, Region capture, and Settings. Output streams into a terminal-style log. |
| **Viewer** | `/viewer` | A read-only broadcast display. Big centered translation, source text + meta in a footer, live/connecting status. For a second screen or browser source. |
| **Projector** | `/projector` | A fullscreen, transparent-friendly caption overlay for streaming/projection. Auto-fits caption size, fades after a hold, position/size/source/backdrop controls on hover. Reads the same `/broadcast` WebSocket. |

All three subscribe to a **broadcast bus** (`TranslationBroadcastHub`, a WebSocket at
`/broadcast` + `/broadcast/latest`). The workbench translates; viewer & projector display.

## Sources given

- **Codebase (read-only):** `Pages/` local folder, also copied to `uploads/`:
  - `AppWorkbenchPage.cs` — the full workbench (HTML+CSS+JS in a C# string), ~3,275 lines. The canonical source of the design system.
  - `BroadcastViewerPage.cs` — the `/viewer` page.
  - `ProjectorPage.cs` — the `/projector` caption overlay.
  - `TranslationBroadcastHub.cs` — WebSocket fan-out hub.
  - `TranslationBroadcastMessage.cs` — the broadcast payload record (sourceText, translatedText, source, target, mode, provider, glossary, engine, latencyMs, cacheHit, sourceKind, segment timing, confidence, displayUntil…).
- Namespaces seen: `Verbeam.Api.Pages`, `Verbeam.Api.Broadcast`, `LocalTranslateHub.Api.*`.

> No Figma, slide template, or logo image files were provided. The brand is **purely
> typographic** — there is no logo asset, only the `OC` mark + `Verbeam` wordmark and
> the `LTH` rail badge, all set in mono. See ICONOGRAPHY.

---

## CONTENT FUNDAMENTALS

The voice is **terse, technical, lowercase, instrument-like** — this reads like a piece
of pro tooling (a DAW, a terminal, a video switcher), not a friendly consumer app.

- **Casing is meaningful and consistent:**
  - **lowercase mono** for field labels, status, modes, meta, terminal tags: `source`, `target`, `engine`, `model`, `provider`, `interval ms`, `broadcast`, `live`, `142 ms`, `ja to zh-TW`.
  - **Title Case** for primary navigation and buttons: `Translate`, `OCR + Translate`, `Audio`, `Region`, `Settings`; `Run`, `Clear`, `Capture Screen`, `Loop Off`, `Stop`.
  - **Sentence case** for settings row titles + full-sentence descriptions: *“Prompt mode” / “Prompt preset used when the translation request does not override mode.”*
- **Person:** essentially impersonal. No "you", no "we", no marketing. Copy names the
  thing (`Language pair`, `Model recommendation`) and describes it factually.
- **Status language is short and declarative:** `Connecting`, `Live`, `Reconnecting`,
  `Waiting for translation`, `OCR text will appear here.`, `Verbeam app ready`, `source is empty`.
- **Terminal log style:** each run appends a tagged entry — a left tag (`source`,
  `translation`, `ocr`, `region`, `system`, `boot`) and a right meta (provider/model,
  latency, time). Errors are plain: `loop every 1500 ms`, `source is empty`.
- **Numbers carry units, lowercased:** `1500 ms`, `142 ms`, `interval ms`.
- **No emoji. No exclamation. No hype.** Hints are quiet and factual (the one amber
  Notice tone is the loudest the UI gets), e.g. OCR engine guidance like
  *“預設本機 OCR，輕量，適合一般文字”* (CJK copy appears verbatim where the workload is CJK).
- **Vibe:** a calm, dark control surface for a power user who is mid-task. Every word
  earns its place; nothing is decorative.

---

## VISUAL FOUNDATIONS

**Overall feeling:** near-black, flat, hairline-ruled, mono-labelled. A workbench. Color
is rationed — greys carry the structure, one blue carries action/active, and
green/amber/red carry state. No gradients, no glow, no skeuomorphism.

- **Color** (`tokens/colors.css`):
  - Background ramp is almost monochrome black: app `#090909`, rail `#121212`, panel `#161616`, raised controls `#1e1e1e`. Lines are `#1f1f1f` (hairline) and `#2d2d2d` (control).
  - Text: primary `#e8e8e8`, muted `#7a7a7a`, faint `#555`, mono-label grey `#777`, slate meta `#94a3b8`.
  - **One accent: blue `#3b82f6`** (hover `#2563eb`) — primary buttons, active tab/nav fill (`rgba(59,130,246,0.15)` + inset `rgba(59,130,246,0.2)` ring + blue text), focus, links, rail-active wash.
  - State accents: green `#22c55e` (live/success/result), amber `#d6b86a` (idle/warning/notice), red `#ef4444` → `#dc2626` (error/danger). A legacy region-select blue `#2f6fff` is used only for the screen-capture marquee.
- **Type** (`tokens/typography.css`, `tokens/fonts.css`): **Outfit** (geometric humanist sans, the UI face) + **JetBrains Mono** (labels, chrome, status, meta, code). Both from Google Fonts. Body is **13px / 1.5**. Mono labels are ~11px, weight 650, grey. Fine-grained weights are used (500 / 600 / 640–650 / 760 for the projector caption).
- **Spacing / layout** (`tokens/spacing.css`): compact 2px-based scale. Controls are **34px** tall (pills 22–26px). The workbench is a CSS-grid shell: rail `~53px` · sidebar `~230px` · fluid center · inspector `~320px`, topbar `44px`, footer `30px`. Collapses the inspector < 1160px and stacks < 760px.
- **Backgrounds:** solid near-black everywhere; **no images, gradients, patterns or textures** in chrome. The projector is pure black so it can key/overlay video; captions get heavy text-shadow (and an optional translucent black backdrop behind glyphs) for legibility over arbitrary footage.
- **Borders & corners:** 1px hairlines do almost all the separation. Radii are small: 4–6px for controls/panels, 8px for cards/terminal/dialogs, 12px for the viewer translation stage, pill `999px` for status capsules.
- **Cards:** flat — a hairline border, a slightly raised fill (`#161616`–`#1a1a1a`), small radius, **no drop shadow**. Titled panels have a mono title bar over a 12px-gap body.
- **Shadows:** essentially unused in chrome. Reserved for (a) overlays/popovers (`0 16px 36px rgba(0,0,0,.5)`) and (b) projector caption legibility (`0 3px 28px #000, 0 0 6px #000…`).
- **Transparency & blur:** sparing. Subtle white washes for hover (`rgba(255,255,255,0.08)`) and faint fills (`rgba(255,255,255,0.02–0.03)`). Backdrop-blur only on the projector HUD/controls (`blur(12–20px)`) floating over video.
- **Animation:** quick and unshowy. `all .15s ease` on controls; `.2s` on rail links; the projector caption fades opacity+translate over `180ms`, and its control bar slides up `250ms cubic-bezier(.4,0,.2,1)` on hover. No bounces, no looping decoration.
- **Hover / press states:** buttons → border lightens to `#4b4b4b` + `rgba(255,255,255,0.08)` wash; primary/danger → darker shade (`#2563eb` / `#dc2626`); nav/tab → blue-tint fill + blue text when active. No scale/press-shrink.
- **Status dots:** a 7px circle, amber by default, green when `.live`.
- **Scrollbars are hidden** on every surface (`scrollbar-width:none` + `::-webkit-scrollbar{display:none}`).
- **Selection:** blue wash (`rgba(59,130,246,0.36)`).

---

## ICONOGRAPHY

- **Icon system: [RemixIcon](https://remixicon.com) v4.2.0**, loaded from CDN
  (`https://cdn.jsdelivr.net/npm/remixicon@4.2.0/fonts/remixicon.css`). It is an icon
  **font** — icons are `<i class="ri-…">` elements colored via `color`, sized ~14–18px,
  rendered in muted grey (`#7a7a7a`) and brightening on active/hover.
- This is the **only** icon source. There are no bespoke SVGs, no PNG icons, and **no emoji**.
- Icons in use (copy these class names):
  - Rail / surfaces: `ri-terminal-box-line` (Workbench), `ri-broadcast-line` (Viewer), `ri-projector-line` (Projector), `ri-pulse-line` (Health).
  - Workspace modes: `ri-translate-2` (Translate), `ri-scan-2-line` (OCR), `ri-bubble-chart-line` (OCR+Translate), `ri-mic-line` (Audio), `ri-voiceprint-line` (Audio+Translate), `ri-focus-3-line` (Region), `ri-settings-3-line` (Settings).
  - Runtime metrics: `ri-database-2-line` (provider), `ri-cpu-line` (model), `ri-file-text-line` (ocr), `ri-sound-module-line` (asr), `ri-server-line` (cache).
- **Brand marks (typographic, not images):** the `OC` mark (muted mono, 11px/650) +
  `Verbeam` wordmark (`#f1f1f1`, mono, 12px/650) in the topbar; the `LTH` rail badge
  (mono, 10px/650, bordered chip). Recreate these with `<span>`s, never as an image.
- Status uses a CSS dot (no icon). Units/`ms` are plain text.

---

## INDEX

**Global entry:** `styles.css` → imports `tokens/{fonts,colors,typography,spacing,effects,base}.css`.
RemixIcon must be linked separately (CDN) on any surface that uses icons.

**Tokens** (`tokens/`): `colors.css` (base ramp + accents + semantic aliases),
`typography.css` (Outfit/JetBrains scale), `fonts.css` (Google Fonts import),
`spacing.css` (scale + shell dims), `effects.css` (radii, borders, shadows, blur, motion),
`base.css` (reset, body defaults, hidden scrollbars, `.vb-label`).

**Components** (`components/`, namespace `window.LocalTranslateHubDesignSystem_32566a`):
- `forms/` — **Button** (primary/secondary/ghost/danger, `commandKey`), **Input** (single/multiline mono), **Select**, **Field** (mono label + control + hint).
- `feedback/` — **Pill** (status capsule + dot), **Message** (terminal log entry, kind-tinted), **StatusLine** (label/value), **Notice** (amber hint).
- `navigation/` — **NavButton** (sidebar), **Tab** (composer mode).
- `surfaces/` — **Panel** (titled card), **Metric** (icon/label/value runtime row), **SettingsRow** (settings-v2 row).

**UI kits** (`ui_kits/`):
- `workbench/` — the `/app` workbench (Translate + Settings screens).
- `viewer/` — the `/viewer` broadcast display.
- `projector/` — the `/projector` caption overlay.

**Foundation cards** (`guidelines/`): typography, color, spacing & effect specimens for the Design System tab.

**`SKILL.md`** — Agent-Skills-compatible entry for using this system standalone.
