# Viewer UI kit (`/viewer`)

A faithful recreation of the Verbeam **broadcast viewer** — a read-only second-screen /
browser-source display. Big centered translation, source + meta in the footer, a
live/connecting status capsule.

## Run
Open `index.html`. A demo loop stands in for the live `/broadcast` WebSocket: status
flips to **Live** and sample ja→zh-TW translations cycle every few seconds.

## Notes
- Single static HTML file (no React, no DS bundle) — mirrors the shipping page, which is
  also a self-contained page.
- The real page subscribes to `wss://…/broadcast` and renders `translation` messages.
