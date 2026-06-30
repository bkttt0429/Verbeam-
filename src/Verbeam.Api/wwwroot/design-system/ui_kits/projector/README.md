# Projector UI kit (`/projector`)

A faithful recreation of the Verbeam **projector** — a fullscreen, transparent-friendly
caption overlay for streaming / projection over arbitrary video. Pure-black stage,
heavy text-shadow + optional translucent backdrop for legibility, a mono HUD
(status + meta) and a hover control bar (size / position / source / backdrop / clear).

## Run
Open `index.html`. A demo loop cycles sample captions. In the real app the caption
auto-fits its size, fades after a hold (`displayUntil` / fade seconds), and persists
its size/position/source/backdrop settings to `localStorage`.

## Notes
- Single static HTML file (no React, no DS bundle) — mirrors the shipping page.
- The real page reads `/broadcast` (WebSocket) + `/broadcast/latest` and de-dupes by a
  caption key; controls here are visual only.
