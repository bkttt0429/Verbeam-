# Workbench UI kit (`/app`)

A faithful, interactive recreation of the Verbeam translation **workbench** — the main
desktop app. Composes the design-system primitives; does not re-implement them.

## Run
Open `index.html`. It renders the full 3-pane shell. Try:
- Click workspace items in the sidebar (Translate / OCR / … / **Settings**).
- Type in the **source** field and hit **Run** — a fake local ja→zh-TW translation
  appends to the terminal log, latency + broadcast state update.
- **Clear** empties the terminal.
- Open **Settings** to browse the settings-v2 row sections (General / Providers /
  Sound / OCR / Audio / Region / Broadcast).

## Files
- `index.html` — mounts the app, loads React + Babel + the DS bundle + RemixIcon.
- `Chrome.jsx` — `Topbar`, `ActivityRail`, `Footer` (the fixed shell chrome).
- `Sidebar.jsx` — `Sidebar` (workspace nav + runtime metrics) and `Inspector` (right rail).
- `Composer.jsx` — `Terminal` (message log) and `Composer` (mode tabs + source/result + actions).
- `SettingsScreen.jsx` — the settings nav + row sections.
- `WorkbenchApp.jsx` — state + assembly; mounts to `#root`.

## Notes
- Translation is faked from a small local dictionary (`WorkbenchApp.jsx`); there is no
  real provider, OCR, ASR, screen capture or WebSocket — those are cosmetic in the kit.
- Marked as a **starting point** (`@startingPoint`) so consuming projects can seed from it.
