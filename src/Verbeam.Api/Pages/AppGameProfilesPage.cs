namespace Verbeam.Api.Pages;

/// <summary>
/// Standalone game-profile manager served at <c>/profiles</c>. Deliberately a small, self-contained
/// page (like the viewer/projector pages) rather than surgery on the 20k-line workbench string: it
/// creates/edits/activates/deletes the profile "shell" (name, window binding, translation settings)
/// against the existing /game-profiles API. Region framing stays in the tray ("Capture Profile
/// Regions…"); this page preserves any existing regions on save.
/// </summary>
public static class AppGameProfilesPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Verbeam · Game Profiles</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #090909; --layer: #121212; --layer-2: #1c1c1c; --line: #1f1f1f; --line-strong: #2d2d2d;
      --text: #e8e8e8; --muted: #7a7a7a; --blue: #3b82f6; --green: #22c55e; --amber: #d6b86a; --red: #ef4444;
      --sans: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
      --mono: "JetBrains Mono", "SF Mono", Consolas, monospace;
    }
    * { box-sizing: border-box; }
    body { margin: 0; background: var(--bg); color: var(--text); font-family: var(--sans); font-size: 14px; line-height: 1.5; }
    header { height: 48px; display: flex; align-items: center; justify-content: space-between; gap: 1rem;
      padding: 0 18px; border-bottom: 1px solid var(--line); background: var(--layer); }
    .brand { font-weight: 700; letter-spacing: 0.02em; }
    header a { color: var(--muted); text-decoration: none; font-size: 13px; }
    header a:hover { color: var(--text); }
    #status { font-size: 12px; color: var(--muted); min-height: 18px; }
    #status.ok { color: var(--green); }
    #status.err { color: var(--red); }
    main { display: grid; grid-template-columns: minmax(320px, 1fr) minmax(360px, 1.2fr); gap: 18px; padding: 18px; align-items: start; }
    @media (max-width: 820px) { main { grid-template-columns: 1fr; } }
    section { border: 1px solid var(--line); border-radius: 10px; background: var(--layer); padding: 16px; }
    h2 { margin: 0 0 12px; font-size: 13px; font-weight: 600; color: var(--muted); text-transform: uppercase; letter-spacing: 0.05em; }
    .card { border: 1px solid var(--line); border-radius: 8px; background: var(--layer-2); padding: 10px 12px; margin-bottom: 8px;
      display: flex; align-items: center; gap: 10px; }
    .card.active { border-color: var(--green); }
    .card-main { flex: 1; min-width: 0; }
    .card-name { font-weight: 600; }
    .card-sub { color: var(--muted); font-size: 12px; font-family: var(--mono); margin-top: 2px; word-break: break-word; }
    .badge { font-size: 10px; color: var(--green); border: 1px solid var(--green); border-radius: 99px; padding: 1px 6px; margin-left: 6px; vertical-align: middle; }
    .card-actions { display: flex; gap: 6px; flex-shrink: 0; }
    .empty { color: var(--muted); padding: 8px 2px; }
    .row { margin-bottom: 12px; }
    label { display: block; font-size: 12px; color: var(--muted); margin-bottom: 4px; }
    input, select { width: 100%; padding: 8px 10px; border: 1px solid var(--line-strong); border-radius: 6px;
      background: var(--bg); color: var(--text); font-family: var(--sans); font-size: 13.5px; }
    input:focus, select:focus { outline: none; border-color: var(--blue); }
    .grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .hint { font-size: 11.5px; color: var(--muted); margin-top: 4px; }
    .regions { font-size: 12px; color: var(--amber); font-family: var(--mono); }
    .actions { display: flex; gap: 8px; margin-top: 16px; flex-wrap: wrap; }
    button { padding: 8px 14px; border: 1px solid var(--line-strong); border-radius: 6px; background: var(--layer-2);
      color: var(--text); font-size: 13px; font-weight: 500; cursor: pointer; }
    button:hover { border-color: var(--blue); }
    button.primary { background: var(--blue); border-color: var(--blue); color: #fff; }
    button.danger { color: var(--red); border-color: transparent; background: transparent; }
    button.danger:hover { border-color: var(--red); }
    button.mini { padding: 4px 9px; font-size: 12px; }
  </style>
</head>
<body>
  <header>
    <div class="brand">Verbeam · Game Profiles</div>
    <div id="status"></div>
    <a href="/app">Workbench →</a>
  </header>
  <main>
    <section>
      <h2>Profiles</h2>
      <div id="profiles"><div class="empty">Loading…</div></div>
      <button id="pf-new" class="mini" style="margin-top:8px">+ New profile</button>
    </section>

    <section>
      <h2 id="pf-formtitle">New profile</h2>
      <div class="row">
        <label>Name</label>
        <input id="pf-name" placeholder="e.g. Elden Ring">
      </div>
      <div class="grid2">
        <div class="row">
          <label>Binding</label>
          <select id="pf-kind"><option value="window">Window (by process)</option><option value="monitor">Monitor (primary)</option></select>
        </div>
        <div class="row" id="row-process">
          <label>Process name</label>
          <input id="pf-process" placeholder="eldenring (.exe optional)">
        </div>
      </div>
      <div class="row" id="row-title">
        <label>Window title pattern <span class="hint">(optional regex)</span></label>
        <input id="pf-title" placeholder="ELDEN RING">
      </div>
      <div class="grid2">
        <div class="row"><label>Source</label><input id="pf-source" placeholder="auto"></div>
        <div class="row"><label>Target</label><input id="pf-target" placeholder="zh-TW"></div>
      </div>
      <div class="grid2">
        <div class="row"><label>Provider <span class="hint">(blank = default)</span></label><input id="pf-provider" placeholder=""></div>
        <div class="row"><label>Model <span class="hint">(blank = default)</span></label><input id="pf-model" placeholder=""></div>
      </div>
      <div class="grid2">
        <div class="row"><label>Mode <span class="hint">(blank = default)</span></label><input id="pf-mode" placeholder="game_dialogue"></div>
        <div class="row"><label>Glossary id <span class="hint">(optional)</span></label><input id="pf-glossary" placeholder=""></div>
      </div>
      <div class="row">
        <label>Capture regions</label>
        <div id="pf-regions" class="regions"></div>
        <div class="hint">Frame regions from the tray menu → “Capture Profile Regions…” while this profile is active. They are preserved here on save.</div>
      </div>
      <div class="actions">
        <button id="pf-save" class="primary">Save</button>
        <button id="pf-activate">Activate</button>
        <button id="pf-delete" class="danger">Delete</button>
      </div>
    </section>
  </main>

  <script>
    const $ = id => document.getElementById(id);
    let current = null;   // full profile object being edited, or null for a new one
    let activeId = null;
    let statusTimer = 0;

    function setStatus(msg, kind) {
      const el = $("status");
      el.textContent = msg || "";
      el.className = kind || "";
      window.clearTimeout(statusTimer);
      if (msg && kind === "ok") {
        statusTimer = window.setTimeout(() => { el.textContent = ""; el.className = ""; }, 3000);
      }
    }

    function esc(s) {
      return String(s == null ? "" : s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
    }

    async function api(method, url, body) {
      const opt = { method, headers: {} };
      if (body !== undefined) { opt.headers["Content-Type"] = "application/json"; opt.body = JSON.stringify(body); }
      const res = await fetch(url, opt);
      const text = await res.text();
      let data = null;
      try { data = text ? JSON.parse(text) : null; } catch (e) { /* non-JSON body */ }
      if (!res.ok) {
        const msg = (data && (data.errorMessage || data.error)) || text || ("HTTP " + res.status);
        throw new Error(msg);
      }
      return data;
    }

    async function loadProfiles() {
      try {
        const doc = await api("GET", "/game-profiles");
        activeId = doc.activeId || null;
        renderList(doc.profiles || []);
      } catch (e) {
        setStatus("Load failed: " + e.message, "err");
      }
    }

    function bindingLabel(p) {
      if (!p.surface) return "—";
      if (p.surface.kind === "monitor") return "monitor";
      const proc = p.surface.processName || "(no process)";
      return proc + (p.surface.windowTitlePattern ? " · /" + p.surface.windowTitlePattern + "/" : "");
    }

    function renderList(profiles) {
      const box = $("profiles");
      box.innerHTML = "";
      if (!profiles.length) {
        box.innerHTML = '<div class="empty">No profiles yet — create one on the right.</div>';
        return;
      }
      for (const p of profiles) {
        const card = document.createElement("div");
        card.className = "card" + (p.id === activeId ? " active" : "");
        const regions = (p.regions && p.regions.length) || 0;
        const main = document.createElement("div");
        main.className = "card-main";
        main.innerHTML =
          '<div class="card-name">' + esc(p.name || p.id) + (p.id === activeId ? ' <span class="badge">active</span>' : "") + "</div>" +
          '<div class="card-sub">' + esc(bindingLabel(p)) + " · " + regions + " region(s) · " + esc(p.source || "auto") + "→" + esc(p.target || "") + "</div>";
        main.style.cursor = "pointer";
        main.onclick = () => editProfile(p);

        const actions = document.createElement("div");
        actions.className = "card-actions";
        const act = document.createElement("button");
        act.className = "mini";
        act.textContent = p.id === activeId ? "Active" : "Activate";
        act.disabled = p.id === activeId;
        act.onclick = () => activateProfile(p.id);
        const del = document.createElement("button");
        del.className = "mini danger";
        del.textContent = "Delete";
        del.onclick = () => deleteProfile(p.id);
        actions.appendChild(act);
        actions.appendChild(del);

        card.appendChild(main);
        card.appendChild(actions);
        box.appendChild(card);
      }
    }

    function toggleKind() {
      const isWindow = $("pf-kind").value === "window";
      $("row-process").style.display = isWindow ? "" : "none";
      $("row-title").style.display = isWindow ? "" : "none";
    }

    function fillForm(p) {
      current = p || null;
      $("pf-name").value = (p && p.name) || "";
      $("pf-kind").value = (p && p.surface && p.surface.kind) || "window";
      $("pf-process").value = (p && p.surface && p.surface.processName) || "";
      $("pf-title").value = (p && p.surface && p.surface.windowTitlePattern) || "";
      $("pf-source").value = (p && p.source) || "auto";
      $("pf-target").value = (p && p.target) || "zh-TW";
      $("pf-provider").value = (p && p.provider) || "";
      $("pf-model").value = (p && p.model) || "";
      $("pf-mode").value = (p && p.mode) || "";
      $("pf-glossary").value = (p && p.glossaryId) || "";
      const rc = (p && p.regions && p.regions.length) || 0;
      $("pf-regions").textContent = rc + " region(s) saved";
      $("pf-formtitle").textContent = (p && p.id) ? ("Edit · " + (p.name || p.id)) : "New profile";
      const hasId = !!(p && p.id);
      $("pf-delete").style.display = hasId ? "" : "none";
      $("pf-activate").style.display = hasId ? "" : "none";
      toggleKind();
    }

    function editProfile(p) { fillForm(p); window.scrollTo({ top: 0, behavior: "smooth" }); }
    function newProfile() { fillForm(null); $("pf-name").focus(); }

    function readForm() {
      const base = current ? JSON.parse(JSON.stringify(current)) : {};
      base.name = $("pf-name").value.trim();
      const kind = $("pf-kind").value;
      base.surface = {
        kind: kind,
        processName: kind === "window" ? $("pf-process").value.trim() : "",
        windowTitlePattern: kind === "window" ? $("pf-title").value.trim() : "",
        monitorDeviceName: (current && current.surface && current.surface.monitorDeviceName) || null
      };
      base.source = $("pf-source").value.trim() || "auto";
      base.target = $("pf-target").value.trim() || "zh-TW";
      base.provider = $("pf-provider").value.trim();
      base.model = $("pf-model").value.trim();
      base.mode = $("pf-mode").value.trim();
      base.glossaryId = $("pf-glossary").value.trim();
      if (!Array.isArray(base.regions)) base.regions = [];
      return base;
    }

    async function saveProfile() {
      const body = readForm();
      if (!body.name) { setStatus("Name is required.", "err"); $("pf-name").focus(); return; }
      try {
        const saved = await api("PUT", "/game-profiles", body);
        fillForm(saved);
        await loadProfiles();
        setStatus("Saved “" + (saved.name || saved.id) + "”.", "ok");
      } catch (e) {
        setStatus("Save failed: " + e.message, "err");
      }
    }

    async function activateProfile(id) {
      try {
        await api("POST", "/game-profiles/" + encodeURIComponent(id) + "/activate");
        await loadProfiles();
        setStatus("Activated.", "ok");
      } catch (e) {
        setStatus("Activate failed: " + e.message, "err");
      }
    }

    async function deleteProfile(id) {
      if (!window.confirm("Delete this profile? This cannot be undone.")) return;
      try {
        await api("DELETE", "/game-profiles/" + encodeURIComponent(id));
        if (current && current.id === id) newProfile();
        await loadProfiles();
        setStatus("Deleted.", "ok");
      } catch (e) {
        setStatus("Delete failed: " + e.message, "err");
      }
    }

    $("pf-save").onclick = saveProfile;
    $("pf-new").onclick = newProfile;
    $("pf-kind").onchange = toggleKind;
    $("pf-delete").onclick = () => { if (current && current.id) deleteProfile(current.id); };
    $("pf-activate").onclick = () => { if (current && current.id) activateProfile(current.id); };

    newProfile();
    loadProfiles();
  </script>
</body>
</html>
""";
}
