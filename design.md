# Verbeam UI Migration Notes

## WebView to Avalonia migration boundary

- Target app: `app/src/Verbeam.Desktop.Avalonia`.
- Reference app: the existing WebView desktop app launched from `app/dist/Verbeam/Verbeam.Api.exe`.
- Reference source for visual/interaction details: `app/src/Verbeam.Api/Pages/AppWorkbenchPage.cs`.
- Screenshots from the existing app are UI specification references. They are not evidence that WebView should be edited.

## Migration workflow

1. Read the relevant WebView CSS, markup, and JavaScript behavior before editing Avalonia.
2. Convert each WebView detail into an explicit Avalonia checklist item.
3. Implement only in `app/src/Verbeam.Desktop.Avalonia` unless the user explicitly asks to change the existing WebView app.
4. Verify with an Avalonia screenshot, not only a successful build.

## Translate UI details that must be preserved

- Source and Result panels use the WebView proportions, spacing, and header density behavior as the baseline.
- Header metadata should collapse progressively when the Result panel is narrowed; text must not squeeze into unreadable vertical columns.
- Copy is an icon-only action in the Result header and should keep a stable hit target.
- Token usage should be compact by default and reveal details on hover/click.
- Drag resizing must preserve minimum usable panel widths and keep Source/Result content readable.
- OCR, Audio, and Region output panels should expose the same icon-only copy affordance as Translate. Copy actions report status in the page action/status text instead of opening dialogs.

## Workspace module header layout

- Workspace pages such as Document, OCR, Audio, and Region should use the same `moduleHeader` visual pattern in Avalonia.
- Header icon badges must have fixed dimensions and `MinWidth`; never allow the icon or text label inside it to compress when the window narrows.
- The title/subtitle area should occupy the star-width center column, with title and subtitle using `TextTrimming="CharacterEllipsis"` instead of overflowing or clipping under the right-side actions.
- The right-side actions must remain vertically centered and keep stable button hit targets. If the window becomes too narrow, metadata text should collapse/truncate before buttons lose shape.
- Do not use tiny generic pills for page identity headers; use a 40-44 px visual badge plus a clear title/subtitle stack so the first viewport identifies the active workspace.
- Apply this pattern consistently to every workspace header. Mixing `moduleHeader` with small pill thumbnails creates inconsistent compression bugs across OCR, Document, Audio, and Region.

## Splitter containment and resizing

- Splitter-controlled panels must stay inside their workspace container. The content host and every split grid should use clipping so a dragged panel cannot visually cover the sidebar or escape the page frame.
- Horizontal and vertical splitters should normalize their first/last pane ratio after resize; do not allow either side to remain below roughly one fifth of the available space.
- Splitter metadata/header controls should collapse or truncate before the panel layout becomes unreadable.
- Do not rely on `MinWidth` alone for splitter safety. In narrow windows, large minimums can force overflow; combine reasonable minimums with clipping and ratio clamping.
- Do not give splitter panes a fixed child `MinWidth` that exceeds the possible narrow layout. A 240 px minimum on both sides of a two-pane split will force the whole grid wider than its parent and make the page look like it moved under the sidebar.
- Clipping is only a last visual guard. The layout itself must remain contained by using flexible child widths plus ratio normalization.
- Splitter constraints must be applied on the `ColumnDefinition` / `RowDefinition`, not only after `PointerReleased`. Otherwise the live drag can still collapse Source to zero and let Result visually cover it.
- Splitter drag feedback should be a low-contrast native micro-interaction, not a bright preview bar. Prefer Avalonia transitions on the splitter hit zone for hover/pressed states; reserve Lottie-style animated assets for empty, success, or error states.

## Responsive shell behavior

- Keep icons and hit targets stable before hiding controls. Narrow layouts should hide text labels and low-priority metadata first, not shrink buttons into unreadable shapes.
- Top tabs use icon + text on wide windows and icon-only with tooltips on compact widths. The active tab still keeps the same selected visual state.
- Sidebar navigation collapses to icon-only before the main workspace is compressed. Runtime and memory cards are hidden in compact sidebar mode.
- Translate header metadata collapses progressively: model/provider/preset fields and result metadata pills hide before Source/Result titles or copy actions disappear.
- Action bars keep primary actions visible; secondary labels can hide while preserving the original click handlers and icon hit targets.
- Translate uses two columns only while there is enough workspace width. In compact shell widths, Source and Result stack vertically with a row splitter so Result cannot cover Source or force unreadable panel headers.
- OCR, Audio, and Region follow the same outer split behavior: wide screens use two panes; compact widths stack the intake/capture pane above the output/result pane while preserving each page's inner splitters.

## Asset and animation library policy

- Keep UI micro-interactions native: use Avalonia `Transitions`, keyframe animations, or composition APIs before adding a package.
- Use one icon family across navigation, tabs, panel headers, and action buttons. Prefer a packaged Avalonia icon library over hand-copied path geometry when the icon appears in multiple places.
- Use Lottie only for non-critical decorative/status moments such as onboarding, empty states, or completion feedback. Do not use Lottie for splitter drag, text panels, or controls that must stay lightweight and exact.
- Avoid full control-suite/theme packages unless a specific missing control justifies the dependency. The native shell already uses Avalonia Fluent theme and should stay visually close to the WebView reference.

## Runtime resource display

- The sidebar memory card must separate native Avalonia UI, backend API, WebView, model runtime, and token usage. Do not show one combined RAM number as if it belongs to the native shell.
- `UI` is the current `Verbeam.Desktop.Avalonia` process memory. `Backend`, `WebView`, and `Model` come from the API memory summary endpoint.
- Keep the compact card readable at sidebar width: labels stay short, values use MB/GB formatting, and details belong in hover/click surfaces rather than expanding the sidebar.
- Use this split when comparing WebView and Avalonia. A high total machine RAM number usually includes API, llama.cpp, model weights, and old WebView/tray processes, not only the Avalonia renderer.

## Settings migration structure

- The reference Settings page is `AppWorkbenchPage.cs`, not only the static design-kit `SettingsScreen.jsx`.
- Preserve the old information architecture: left settings navigation grouped as Desktop, Pipelines, and Runtime; the right side shows one active settings panel at a time.
- Common native panels should land first: General, System & Appearance, Performance, Providers, Sound, OCR, Audio, Region, Shortcuts, and Memory.
- General mirrors the active translation route: source, target, prompt mode, glossary, runtime/model, and token summary. Changes should update the active Translate controls immediately.
- Performance is backed by `/shell-settings` and must be a real save/reset flow, including WebView2 GPU mode, browser region preview quality, and custom Chromium flags.
- Pipeline entries inside Settings are settings panels, not workspace shortcuts. OCR must expose engine, language, profile, content route, preference, and preprocess defaults. Audio must expose ASR engine, language, and profile. Region must expose native select/snapshot/stop controls plus min OCR gap and live native status.
- Native Region min OCR gap is a real backend setting for the active loop. Avalonia sends it as `minOcrGapMs` on `/region/native/select` and `/region/native/start`; `/region/native/status` returns the effective `minOcrGapMs`, `watchTickMs`, and `forceRefreshMs`.
- Shortcuts are backed by `/hotkeys`; the native Settings page should at minimum list actions, current bindings, status, reset, refresh, and trigger-test actions before implementing full key recording.
- Memory uses `/system/memory-summary` and must keep Native UI, API, WebView, Model, and GPU as separate cards.
- Appearance and Sound currently have mostly front-end/local behavior in the WebView reference. In Avalonia, do not present them as persisted settings until native storage or backend endpoints exist.
- Do not render a duplicate appearance preview canvas in Avalonia. The live shell itself is the preview; use controls and saved slots instead.
- Providers in Avalonia must replace the old WebView supplier editor in-place, not open `/api-suppliers/new` as the primary flow. Mirror the WebView `provider-compact-layout`: left side is the grouped provider catalog/filter, right side is the selected provider detail with description, model chips, supplier actions, and inline editor/download controls.
- Model selection defaults to showing all relevant entries grouped as Recommended, Installed, and Available. Tencent Hy-MT2 1.8B Q4 remains the realtime/local recommendation; slower quality candidates should not be presented as the default realtime choice.
- API-compatible supplier management should use the existing JSON endpoints: `/translation/api-supplier-presets`, `/translation/api-suppliers`, `/translation/api-suppliers/{id}/test`, `/models/fetch`, and `/activate`. Add/Edit belongs inline on the Providers page so theme, layout, and state remain consistent.
- Keep provider cards concise. Do not stack every badge in the title row; show one title, one route/status line, and move extra status into hover/detail text or the right-side supplier lane.
- Provider/vendor logos must remain vector quality when full logos are shown. Use the original SVG asset through a real SVG renderer for large/detail branding; do not rasterize provider logos into low-resolution bitmaps.
- Compact provider/model lists should reuse the existing WebView provider icon assets from `Verbeam.Api/wwwroot/design-system/icons`. SVG assets must go through a real SVG renderer such as `Svg.Skia` before display; do not use ad-hoc path-only SVG parsing because gradients, transforms, masks, and clip paths will be lost or cropped. Fall back to controlled avatar initials only when an icon asset is missing or fails to render.
- Provider detail model cards should prefer compact, contained chips. On the current Settings width, use a single-column compact list with ellipsis and tooltips; only use two-column model chips when the detail pane has a measured finite width large enough to avoid horizontal overflow.
- The Providers detail first viewport must show both model selection and API-compatible supplier management. Model chips are a scrollable sub-list with a capped height; they must not push Add/Edit/Test/Fetch supplier controls below the visible page area.
- Providers V4 direction: keep the top header as a compact identity/status toolbar, not a form. Do not repeat target/provider/model dropdowns in the Providers header; show current route/runtime as low-emphasis status pills and let catalog/model selection drive actual changes.
- API-compatible supplier management should be opened intentionally from a `+` action near the provider catalog filters. Do not keep a large API Suppliers management card permanently in the selected-provider detail lane.
- Provider model rows show vendor/logo, model name, and one status badge by default. Move model descriptions, recommendation rationale, install/source details, and warnings into hover tooltips.
- Usage display in Providers should be compact and glanceable. Use small gauge-like status cards or a future chart control for model memory, VRAM/API quota, tokens, and API state; avoid large metric cards that dominate the page.
- Provider usage cards in Avalonia use the native `UsageGauge` control: animated compact gauges on the left, short value text on the right, and detailed interpretation in hover tooltips. VRAM and model memory use proportional liquid/ring gauges; tokens use recent request usage; API uses a simple health ring.
- The `UsageGauge` liquid motion should stay soft and chart-like: circular clipping, low wave amplitude, long wave length, and at most a subtle secondary wave. Avoid sharp multi-layer waves or visibly mixed handmade fills; ECharts liquidfill is the visual reference, but the native app should keep the gauge in Avalonia/Skia rather than reintroducing WebView just for this effect.

### Candidate libraries

- Primary icon set: `FluentIcons.Avalonia`. Best fit for the current Fluent/workbench direction; use for common actions such as back, home, copy, swap, settings, translate, OCR, audio, file, region, and provider status.
- `FluentIcons.Avalonia` is now the adopted first-choice icon package in the Avalonia shell. New repeated UI icons should use `<ic:FluentIcon>` before adding hand-written `PathIcon` geometry.
- `FluentIcon.IconSize` accepts only the package enum values such as `Size16`, `Size20`, `Size24`, and `Size32`. Do not invent intermediate values like `Size18`; use the nearest enum size or `FontSize` when an exact visual size is required.
- Navigation icons and labels must update together when workspace active state changes. Do not leave top-tab or sidebar icon colors hard-coded after the active class changes.
- Supplemental icons: `IconPacks.Avalonia.Lucide`, `IconPacks.Avalonia.Codicons`, `IconPacks.Avalonia.PhosphorIcons`, `IconPacks.Avalonia.RemixIcon`, `IconPacks.Avalonia.SimpleIcons`, and `IconPacks.Avalonia.FileIcons`. Use only when the primary set lacks a clear symbol.
- Alternative icon framework: `Projektanker.Icons.Avalonia` with provider packages such as FontAwesome or MaterialDesign. Consider this when a unified provider model is more useful than one fixed icon family.
- Material fallback: `Material.Icons.Avalonia`. Use sparingly because the visual language is less aligned with the current Verbeam shell.
- SVG asset rendering: `Avalonia.Svg.Skia` or `Svg.Controls.Skia.Avalonia`. Use for external SVG assets, provider logos, and brand marks instead of converting every logo into XAML path geometry by hand.
- Locale assets: `Flags.Icons.Avalonia`. Use only where flags add real recognition value; keep language names/codes as the primary label.
- Lightweight status animation: `Avalonia.Labs.Lottie`. Use for empty states, success, error, and loading illustrations. Do not use for splitters, live translation text panels, or other precision controls.
- Declarative interactions: `Xaml.Behaviors.Avalonia` / `Xaml.Behaviors.Interactions.*`. Consider for reusable drag/drop, responsive, or event-triggered UI behavior when code-behind starts repeating.
- Built-in color tooling: `Avalonia.Controls.ColorPicker`. Prefer this for the appearance editor instead of custom-building color pickers.
- Full theme/control suites to avoid by default: `FluentAvaloniaUI`, `Semi.Avalonia`, and `Material.Avalonia`. They are useful libraries, but they can replace too much of the current visual system unless a specific control justifies the dependency.

## Common pitfall

Do not patch `AppWorkbenchPage.cs` when the task is to restore WebView UI details into Avalonia. WebView is the reference implementation for this migration; Avalonia is the destination.

## 2026-06-26 migration miss: header icons

What went wrong:

- The WebView source was read in narrow CSS/DOM slices, not converted into a full visual checklist.
- The migration treated panel headers as `title + metadata`, but the original visual unit is `icon box + title + metadata`.
- The screenshot showed the reference WebView UI, but it was misread as the active implementation target.

Correction:

- For every screenshot-driven UI task, identify whether the screenshot is reference or target before editing.
- For Avalonia migration, inspect both the WebView source and the current Avalonia XAML.
- Port visual atoms explicitly: icons, hit targets, spacing, collapse behavior, hover/click affordances, and theme contrast.
- Preserve icon aspect ratios during migration. Use a fixed `Viewbox` with a 24x24 canvas/path or a real asset for header icons; do not rely on arbitrary `PathIcon` geometry when it can compress at small sizes.
