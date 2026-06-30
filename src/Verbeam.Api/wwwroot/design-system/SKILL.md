---
name: verbeam-design
description: Use this skill to generate well-branded interfaces and assets for Verbeam (通用翻譯軟體 — a local-first universal translation desktop app with an "OpenCode desktop" dark workbench aesthetic), either for production or throwaway prototypes/mocks/etc. Contains essential design guidelines, colors, type, fonts, assets, and UI kit components for prototyping.
user-invocable: true
---

Read the `readme.md` file within this skill, and explore the other available files.

If creating visual artifacts (slides, mocks, throwaway prototypes, etc), copy assets out and create static HTML files for the user to view. If working on production code, you can copy assets and read the rules here to become an expert in designing with this brand.

If the user invokes this skill without any other guidance, ask them what they want to build or design, ask some questions, and act as an expert designer who outputs HTML artifacts _or_ production code, depending on the need.

## Quick orientation
- **Brand:** Verbeam (codebase name LocalTranslateHub). Marks are typographic only — `OC` mark + `Verbeam` wordmark + `LTH` rail badge in JetBrains Mono. No logo image exists.
- **Aesthetic:** near-black dark workbench, flat, hairline-ruled, monospace-labelled. One blue accent (`#3b82f6`); green/amber/red for state. No gradients, no shadows in chrome, no emoji.
- **Fonts:** Outfit (UI sans) + JetBrains Mono (labels/chrome/meta). Both Google Fonts.
- **Icons:** RemixIcon 4.2.0 via CDN (`<i class="ri-…">`). The only icon source.
- **Surfaces:** Workbench (`/app`), Viewer (`/viewer`), Projector (`/projector`).

## Files
- `styles.css` → links all tokens + fonts. Add the RemixIcon CDN `<link>` separately.
- `tokens/` — colors, typography, fonts, spacing, effects, base.
- `components/` — React primitives (Button, Input, Select, Field, Pill, Message, StatusLine, Notice, NavButton, Tab, Panel, Metric, SettingsRow). Compiled to `_ds_bundle.js`; mount via `window.LocalTranslateHubDesignSystem_<hash>`.
- `ui_kits/{workbench,viewer,projector}/` — full-screen recreations.
- `guidelines/` — foundation specimen cards.
- `readme.md` — full design guide (content fundamentals, visual foundations, iconography, index).
