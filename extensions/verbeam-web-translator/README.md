# Verbeam Web Translator Extension

This is a no-build Chrome/Edge/Firefox extension for translating regular web pages through the local Verbeam API.

It is a clean Verbeam implementation. It does not copy Read Frog extension code.

## Install

1. Start Verbeam:

   ```powershell
   cd D:\LocalTranslateHub\app
   dotnet run --project .\src\Verbeam.Api\Verbeam.Api.csproj
   ```

2. Open your browser's extension management page:

   ```text
   chrome://extensions
   edge://extensions
   about:debugging#/runtime/this-firefox
   ```

3. Enable Developer mode.
4. Choose Load unpacked.
5. Select:

   ```text
   D:\LocalTranslateHub\app\extensions\verbeam-web-translator
   ```

## Use

1. Open any `http` or `https` page.
2. Click the Verbeam extension button.
3. The popup auto-detects the local backend from the current URL, `localhost:5757`, `127.0.0.1:5757`, `localhost:5768`, or `127.0.0.1:5768`. Use **Detect** if you restart Verbeam on a different local port.
4. Choose source, target, provider, model, mode, and context:
   - **Provider / Model** are loaded from the local backend. Local providers such as `llama-cpp` and `ollama` show their available models in the picker.
   - **Fast context** skips page context and memory context and is the default for local models.
   - **Balanced context** sends a short visible-page sample.
   - **Full page context** sends a larger page sample and can be much slower on local LLMs.
5. Choose display mode:
   - **Bilingual** (default): keeps the original text and shows the translation underneath.
   - **Translation only**: replaces the paragraph text with the translated text.
6. Click Translate Page.

The extension scans paragraph-level blocks, translates them through `POST /translate/web`, and renders the result based on the selected display mode. Blocks are translated lazily as they enter the viewport, nearby blocks are prioritized ahead of far-off content, and new content added by AJAX/SPAs is picked up with a debounced rescan when Watch AJAX updates is enabled. Open shadow roots are scanned and watched when the browser exposes them. If page text changes while a request is in flight, the stale result is discarded and the block is watched again. Restore and route changes cancel in-flight backend requests so stale page work stops instead of continuing to occupy local model slots. Restore returns the page to its original state.

The content script also mounts a small floating button on supported pages when enabled in the popup:

- Click **V** to toggle Translate Page / Restore.
- Hover the button for quick controls: **R** restores, **L/U** locks or unlocks dragging, and **X** hides the floating button on the current site.
- Drag the button to save its side and vertical position.

## Development (auto-reload)

The easiest way to develop and test is to run the provided PowerShell launcher. It opens Edge with the extension already loaded and starts the file watcher in one step.

> Note: Recent Google Chrome stable builds block the `--load-extension` command-line flag, so the launcher defaults to **Microsoft Edge**.

```powershell
cd D:\LocalTranslateHub\app\extensions\verbeam-web-translator
.\scripts\start-dev.ps1
```

This will:
- Create a temporary Edge profile in `.dev-profile-edge`
- Launch Edge with `--load-extension` so the extension is pre-installed
- Start `scripts/watch.mjs` to auto-reload on file changes
- Clean up the temporary profile when you press Enter

To use Chrome instead (may be blocked by Chrome stable):

```powershell
.\scripts\start-dev.ps1 -Browser chrome
```

### Manual flow

If you prefer to use your existing browser profile:

1. Start Edge/Chrome with a remote debugging port. Close all browser windows and run:

   ```powershell
   msedge --remote-debugging-port=9222
   # or
   chrome --remote-debugging-port=9222
   ```

2. In another terminal, start the file watcher:

   ```powershell
   cd D:\LocalTranslateHub\app\extensions\verbeam-web-translator
   node scripts/watch.mjs
   ```

3. Manually load the unpacked extension once at `chrome://extensions`.
4. Edit any extension file. The watcher reloads the extension automatically.
5. Refresh the test page so the updated content script is injected.

> Note: `scripts/start-dev.ps1`, `scripts/watch.mjs`, and `scripts/reload.mjs` are optional development helpers. The extension itself remains a no-build unpacked extension.

## AJAX Check

After clicking Translate Page, open DevTools on the page and run:

```js
setTimeout(() => {
  const p = document.createElement("p");
  p.textContent = "This sentence was inserted after translation started.";
  p.style.fontSize = "22px";
  p.style.margin = "24px";
  document.body.appendChild(p);
}, 2000);

setTimeout(() => {
  const p = document.createElement("p");
  const text = document.createTextNode("");
  p.appendChild(text);
  p.style.fontSize = "22px";
  p.style.margin = "24px";
  document.body.appendChild(p);

  setTimeout(() => {
    text.textContent = "This sentence was filled later by an AJAX-style text update.";
  }, 1000);
}, 4000);
```

If Watch AJAX updates is enabled, both late-added sentences should be translated automatically.
