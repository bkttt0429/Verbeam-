namespace YomiBridge.Api.Pages;

public static class AppWorkbenchPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>YomiBridge App</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #0b0d10;
      --rail: #11151a;
      --panel: #151a20;
      --panel-2: #1b222a;
      --line: #2b343f;
      --line-soft: #202832;
      --text: #e9edf0;
      --muted: #8d98a6;
      --faint: #687483;
      --green: #74d99f;
      --cyan: #73d6ff;
      --amber: #e7bd5f;
      --red: #ee7b7b;
      --shadow: rgba(0, 0, 0, 0.35);
      --mono: "Cascadia Mono", "SFMono-Regular", Consolas, monospace;
      --sans: "Inter", "Segoe UI", system-ui, sans-serif;
    }

    * {
      box-sizing: border-box;
    }

    html,
    body {
      min-height: 100%;
    }

    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-family: var(--sans);
      letter-spacing: 0;
    }

    button,
    input,
    select,
    textarea {
      font: inherit;
    }

    button,
    select,
    input[type="file"]::file-selector-button {
      min-height: 34px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--panel-2);
      color: var(--text);
    }

    button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 8px;
      padding: 0 12px;
      cursor: pointer;
    }

    button:hover {
      border-color: var(--cyan);
    }

    button.primary {
      border-color: rgba(116, 217, 159, 0.48);
      background: #173022;
      color: #dffbea;
    }

    button.danger {
      border-color: rgba(238, 123, 123, 0.42);
      color: #ffdede;
    }

    button:disabled {
      cursor: not-allowed;
      opacity: 0.55;
    }

    input,
    select,
    textarea {
      width: 100%;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: #0d1116;
      color: var(--text);
      outline: none;
    }

    input,
    select {
      min-height: 34px;
      padding: 0 10px;
    }

    textarea {
      min-height: 132px;
      resize: vertical;
      padding: 10px;
      line-height: 1.5;
      font-family: var(--mono);
      font-size: 13px;
    }

    textarea:focus,
    input:focus,
    select:focus {
      border-color: var(--cyan);
    }

    .shell {
      min-height: 100vh;
      display: grid;
      grid-template-columns: 240px minmax(0, 1fr) 320px;
      grid-template-rows: 44px minmax(0, 1fr) 34px;
      background:
        linear-gradient(90deg, rgba(115, 214, 255, 0.04), transparent 34%),
        var(--bg);
    }

    .topbar {
      grid-column: 1 / 4;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      border-bottom: 1px solid var(--line-soft);
      background: #0d1116;
      padding: 0 14px;
      box-shadow: 0 1px 0 var(--shadow);
    }

    .brand {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      min-width: 0;
      font-family: var(--mono);
      font-size: 13px;
      font-weight: 700;
    }

    .mark {
      display: inline-grid;
      place-items: center;
      width: 24px;
      height: 24px;
      border: 1px solid var(--line);
      border-radius: 5px;
      color: var(--green);
      background: #101820;
    }

    .top-actions {
      display: flex;
      align-items: center;
      gap: 8px;
      color: var(--muted);
      font-family: var(--mono);
      font-size: 12px;
      min-width: 0;
    }

    .pill {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      min-height: 28px;
      padding: 0 9px;
      border: 1px solid var(--line);
      border-radius: 999px;
      background: var(--rail);
      white-space: nowrap;
    }

    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--amber);
    }

    .dot.live {
      background: var(--green);
    }

    .sidebar {
      grid-row: 2 / 3;
      border-right: 1px solid var(--line-soft);
      background: var(--rail);
      display: grid;
      grid-template-rows: auto auto minmax(0, 1fr);
      min-width: 0;
    }

    .side-section {
      padding: 14px;
      border-bottom: 1px solid var(--line-soft);
    }

    .side-title,
    .panel-title,
    .field label {
      color: var(--muted);
      font-family: var(--mono);
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
    }

    .nav {
      display: grid;
      gap: 6px;
      margin-top: 10px;
    }

    .nav button {
      justify-content: flex-start;
      width: 100%;
      background: transparent;
      color: var(--muted);
    }

    .nav button.active {
      border-color: rgba(115, 214, 255, 0.5);
      background: #10202a;
      color: var(--text);
    }

    .metric-grid {
      display: grid;
      gap: 8px;
      margin-top: 10px;
    }

    .metric {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      min-height: 32px;
      border: 1px solid var(--line-soft);
      border-radius: 6px;
      padding: 0 9px;
      color: var(--muted);
      font-family: var(--mono);
      font-size: 12px;
    }

    .metric strong {
      color: var(--text);
      font-weight: 700;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .session-list {
      overflow: auto;
      padding: 10px;
      display: grid;
      align-content: start;
      gap: 8px;
    }

    .session-item {
      border: 1px solid var(--line-soft);
      border-radius: 6px;
      padding: 9px;
      background: #0f141a;
      color: var(--muted);
      font-family: var(--mono);
      font-size: 12px;
      line-height: 1.35;
    }

    .session-item strong {
      display: block;
      color: var(--text);
      margin-bottom: 4px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .workspace {
      grid-row: 2 / 3;
      min-width: 0;
      display: grid;
      grid-template-rows: minmax(0, 1fr) auto;
    }

    .terminal {
      min-height: 0;
      overflow: auto;
      padding: 16px;
      display: grid;
      align-content: start;
      gap: 10px;
    }

    .message {
      border: 1px solid var(--line-soft);
      border-radius: 8px;
      background: rgba(21, 26, 32, 0.84);
      box-shadow: 0 12px 30px var(--shadow);
      overflow: hidden;
    }

    .message-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      min-height: 34px;
      padding: 0 10px;
      border-bottom: 1px solid var(--line-soft);
      color: var(--muted);
      font-family: var(--mono);
      font-size: 12px;
    }

    .message-body {
      padding: 12px;
      white-space: pre-wrap;
      word-break: break-word;
      line-height: 1.55;
      font-size: 14px;
    }

    .message.user .message-head {
      color: var(--cyan);
    }

    .message.result .message-head {
      color: var(--green);
    }

    .message.error .message-head {
      color: var(--red);
    }

    .composer {
      border-top: 1px solid var(--line-soft);
      background: #0d1116;
      padding: 12px;
      display: grid;
      gap: 10px;
    }

    .tabs {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
    }

    .tabs button {
      min-width: 82px;
      background: transparent;
      color: var(--muted);
    }

    .tabs button.active {
      border-color: rgba(116, 217, 159, 0.48);
      background: #13231b;
      color: var(--text);
    }

    .input-grid {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(280px, 34%);
      gap: 10px;
    }

    .field {
      display: grid;
      gap: 6px;
      min-width: 0;
    }

    .actions {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      min-width: 0;
    }

    .actions-left,
    .actions-right {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
      flex-wrap: wrap;
    }

    .command-key {
      color: var(--green);
      font-family: var(--mono);
      font-weight: 700;
    }

    .inspector {
      grid-row: 2 / 3;
      border-left: 1px solid var(--line-soft);
      background: var(--rail);
      min-width: 0;
      overflow: auto;
      display: grid;
      align-content: start;
      gap: 12px;
      padding: 14px;
    }

    .panel {
      border: 1px solid var(--line-soft);
      border-radius: 8px;
      background: var(--panel);
      overflow: hidden;
    }

    .panel-title {
      min-height: 34px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      border-bottom: 1px solid var(--line-soft);
      padding: 0 10px;
    }

    .panel-body {
      display: grid;
      gap: 10px;
      padding: 10px;
    }

    .status-line {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      color: var(--muted);
      font-family: var(--mono);
      font-size: 12px;
    }

    .status-line strong {
      color: var(--text);
      font-weight: 700;
    }

    .notice {
      border: 1px solid rgba(231, 189, 95, 0.38);
      border-radius: 6px;
      background: rgba(231, 189, 95, 0.08);
      color: #f4d89a;
      padding: 9px;
      line-height: 1.45;
      font-size: 12px;
    }

    .drop-zone {
      min-height: 86px;
      display: grid;
      gap: 8px;
      align-content: center;
      border: 1px dashed var(--line);
      border-radius: 8px;
      background: #0d1116;
      padding: 10px;
    }

    .drop-zone.dragging {
      border-color: var(--cyan);
      background: #10202a;
    }

    .drop-title {
      color: var(--text);
      font-family: var(--mono);
      font-size: 13px;
      font-weight: 700;
    }

    .field-hint {
      color: var(--muted);
      line-height: 1.45;
      font-size: 12px;
    }

    .region-pane {
      display: grid;
      gap: 10px;
    }

    .region-toolbar {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
      min-width: 0;
    }

    .region-toolbar .field {
      width: 118px;
    }

    .region-toolbar input {
      font-family: var(--mono);
    }

    .region-stage {
      position: relative;
      min-height: 220px;
      max-height: 340px;
      overflow: hidden;
      border: 1px dashed var(--line);
      border-radius: 8px;
      background: #090c10;
      user-select: none;
      cursor: crosshair;
    }

    .region-stage.active {
      border-color: rgba(115, 214, 255, 0.5);
      background: #0b1218;
    }

    .region-stage video {
      display: block;
      width: 100%;
      max-height: 340px;
      background: #05070a;
    }

    .region-placeholder {
      position: absolute;
      inset: 0;
      display: grid;
      place-items: center;
      padding: 20px;
      color: var(--muted);
      text-align: center;
      line-height: 1.45;
      pointer-events: none;
    }

    .region-selection {
      position: absolute;
      border: 2px solid var(--green);
      background: rgba(116, 217, 159, 0.12);
      box-shadow: 0 0 0 9999px rgba(0, 0, 0, 0.42);
      pointer-events: none;
    }

    .region-output-grid {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
      gap: 10px;
    }

    .speech-table-wrap {
      grid-column: 1 / -1;
      max-height: 220px;
      overflow: auto;
      border: 1px solid var(--line-soft);
      border-radius: 8px;
      background: #0d1116;
    }

    .speech-table {
      width: 100%;
      border-collapse: collapse;
      font-family: var(--mono);
      font-size: 12px;
    }

    .speech-table th,
    .speech-table td {
      border-bottom: 1px solid var(--line-soft);
      padding: 7px 8px;
      text-align: left;
      vertical-align: top;
    }

    .speech-table th {
      color: var(--muted);
      font-weight: 700;
      text-transform: uppercase;
    }

    .speech-table td:nth-child(1),
    .speech-table td:nth-child(2) {
      white-space: nowrap;
      color: var(--faint);
    }

    .footer {
      grid-column: 1 / 4;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      border-top: 1px solid var(--line-soft);
      background: #0d1116;
      color: var(--muted);
      font-family: var(--mono);
      font-size: 12px;
      padding: 0 14px;
      min-width: 0;
    }

    .footer span {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .hidden {
      display: none;
    }

    @media (max-width: 1060px) {
      .shell {
        grid-template-columns: 210px minmax(0, 1fr);
      }

      .inspector {
        display: none;
      }

      .topbar,
      .footer {
        grid-column: 1 / 3;
      }
    }

    @media (max-width: 760px) {
      .shell {
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: 44px auto minmax(0, 1fr) auto;
      }

      .topbar,
      .footer {
        grid-column: 1 / 2;
      }

      .sidebar {
        grid-row: 2 / 3;
        border-right: 0;
      }

      .side-section:last-child,
      .session-list {
        display: none;
      }

      .workspace {
        grid-row: 3 / 4;
      }

      .input-grid {
        grid-template-columns: minmax(0, 1fr);
      }

      .region-output-grid {
        grid-template-columns: minmax(0, 1fr);
      }

      .top-actions .pill:nth-child(2) {
        display: none;
      }
    }
  </style>
  <style>
    :root {
      --oc-bg: #0f0f0f;
      --oc-titlebar: #242424;
      --oc-rail: #101010;
      --oc-base: #141414;
      --oc-layer: #191919;
      --oc-layer-2: #202020;
      --oc-hover: #292929;
      --oc-line: #2d2d2d;
      --oc-line-strong: #3a3a3a;
      --oc-text: #dddddd;
      --oc-muted: #8b8b8b;
      --oc-faint: #606060;
      --oc-blue: #2f6fff;
      --oc-green: #72d67c;
      --oc-amber: #d6b86a;
      --oc-red: #ff7b72;
      --bg: var(--oc-bg);
      --rail: var(--oc-rail);
      --panel: var(--oc-layer);
      --panel-2: var(--oc-layer-2);
      --line: var(--oc-line);
      --line-soft: var(--oc-line);
      --text: var(--oc-text);
      --muted: var(--oc-muted);
      --faint: var(--oc-faint);
      --green: var(--oc-green);
      --cyan: var(--oc-blue);
      --amber: var(--oc-amber);
      --red: var(--oc-red);
      --shadow: rgba(0, 0, 0, 0.42);
    }

    body {
      background: var(--oc-bg);
      color: var(--oc-text);
      font-size: 13px;
    }

    .shell {
      grid-template-columns: 56px 224px minmax(0, 1fr) 328px;
      grid-template-rows: 32px minmax(0, 1fr) 28px;
      background: var(--oc-bg);
    }

    .topbar {
      grid-column: 1 / 5;
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
      min-height: 32px;
      padding: 0 10px;
      border-bottom-color: #1f1f1f;
      background: var(--oc-titlebar);
      box-shadow: none;
    }

    .brand {
      grid-column: 2;
      gap: 8px;
      justify-self: center;
      color: #f1f1f1;
      font-size: 12px;
      font-weight: 650;
    }

    .mark {
      width: auto;
      height: auto;
      border: 0;
      border-radius: 0;
      background: transparent;
      color: var(--oc-muted);
      font-size: 11px;
      font-weight: 650;
    }

    .top-actions {
      grid-column: 3;
      justify-self: end;
      gap: 6px;
      font-size: 11px;
    }

    .pill {
      min-height: 22px;
      padding: 0 8px;
      border-color: var(--oc-line);
      border-radius: 5px;
      background: var(--oc-layer);
      color: var(--oc-muted);
    }

    .dot {
      width: 7px;
      height: 7px;
    }

    .activity-rail {
      grid-column: 1;
      grid-row: 2 / 3;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: space-between;
      padding: 12px 8px;
      border-right: 1px solid #202020;
      background: var(--oc-rail);
    }

    .rail-top,
    .rail-bottom {
      display: grid;
      gap: 12px;
      justify-items: center;
      width: 100%;
    }

    .rail-badge,
    .rail-link {
      display: inline-grid;
      place-items: center;
      width: 34px;
      min-height: 28px;
      border: 1px solid transparent;
      border-radius: 5px;
      color: var(--oc-muted);
      font-family: var(--mono);
      font-size: 10px;
      font-weight: 650;
      text-decoration: none;
      text-transform: uppercase;
    }

    .rail-badge {
      border-color: var(--oc-line);
      background: #161616;
      color: var(--oc-text);
    }

    .rail-link:hover,
    .rail-link.active {
      border-color: var(--oc-line-strong);
      background: var(--oc-hover);
      color: var(--oc-text);
    }

    .sidebar {
      grid-column: 2;
      grid-row: 2 / 3;
      border-right-color: var(--oc-line);
      background: #121212;
    }

    .side-section {
      padding: 18px 14px;
      border-bottom-color: var(--oc-line);
    }

    .side-title,
    .panel-title,
    .field label {
      color: #777777;
      font-size: 11px;
      font-weight: 640;
      letter-spacing: 0;
    }

    .nav {
      gap: 5px;
      margin-top: 12px;
    }

    .nav button,
    .tabs button {
      min-height: 32px;
      border-color: transparent;
      border-radius: 5px;
      background: transparent;
      color: #969696;
      font-size: 13px;
    }

    .nav button:hover,
    .tabs button:hover {
      border-color: transparent;
      background: #222222;
      color: var(--oc-text);
    }

    .nav button.active,
    .tabs button.active {
      border-color: transparent;
      background: #2a2a2a;
      color: #f1f1f1;
    }

    .metric {
      min-height: 30px;
      border-color: transparent;
      border-radius: 5px;
      background: #171717;
      color: #7c7c7c;
    }

    .metric strong {
      color: #d0d0d0;
      font-weight: 610;
    }

    .session-list {
      padding: 12px;
      gap: 8px;
    }

    .session-item {
      border-color: var(--oc-line);
      border-radius: 7px;
      background: #171717;
      color: var(--oc-muted);
    }

    .session-item strong {
      color: var(--oc-text);
      font-weight: 610;
    }

    .workspace {
      grid-column: 3;
      grid-row: 2 / 3;
      gap: 14px;
      padding: 30px 24px;
      background: #111111;
    }

    .terminal {
      border: 1px solid var(--oc-line);
      border-radius: 8px;
      background: #151515;
      padding: 18px;
      scrollbar-color: var(--oc-line-strong) transparent;
    }

    .message {
      border-color: var(--oc-line);
      border-radius: 8px;
      background: #1a1a1a;
      box-shadow: none;
    }

    .message-head {
      min-height: 32px;
      border-bottom-color: var(--oc-line);
      background: #181818;
      color: var(--oc-muted);
      font-size: 11px;
    }

    .message-body {
      padding: 14px;
      color: #d7d7d7;
      font-size: 13px;
    }

    .message.user .message-head {
      color: #80a7ff;
    }

    .message.result .message-head {
      color: var(--oc-green);
    }

    .message.error .message-head {
      color: var(--oc-red);
    }

    .composer {
      border: 1px solid var(--oc-line);
      border-radius: 8px;
      background: #171717;
      padding: 12px;
    }

    .tabs {
      gap: 4px;
      padding-bottom: 2px;
    }

    .tabs button {
      min-width: 0;
      padding: 0 10px;
      font-size: 12px;
    }

    button,
    select,
    input[type="file"]::file-selector-button {
      min-height: 32px;
      border-color: var(--oc-line-strong);
      border-radius: 6px;
      background: #222222;
      color: #e4e4e4;
    }

    button {
      font-weight: 530;
    }

    button:hover,
    input[type="file"]::file-selector-button:hover {
      border-color: #4b4b4b;
      background: #292929;
    }

    button.primary {
      border-color: #3f72ff;
      background: var(--oc-blue);
      color: #ffffff;
    }

    button.primary:hover {
      background: #3f7cff;
    }

    button.danger {
      border-color: rgba(255, 123, 114, 0.38);
      background: #251717;
      color: #ffd7d3;
    }

    input,
    select,
    textarea {
      border-color: var(--oc-line-strong);
      border-radius: 6px;
      background: #101010;
      color: #eeeeee;
    }

    input,
    select {
      min-height: 32px;
    }

    textarea {
      min-height: 126px;
      font-size: 12.5px;
      line-height: 1.55;
    }

    textarea:focus,
    input:focus,
    select:focus {
      border-color: var(--oc-blue);
      box-shadow: 0 0 0 1px rgba(47, 111, 255, 0.34);
    }

    .inspector {
      grid-column: 4;
      grid-row: 2 / 3;
      gap: 12px;
      padding: 30px 24px 30px 0;
      border-left: 0;
      background: #111111;
    }

    .panel {
      border-color: var(--oc-line);
      border-radius: 8px;
      background: #171717;
    }

    .panel-title {
      min-height: 34px;
      border-bottom-color: var(--oc-line);
      background: #191919;
      color: #b8b8b8;
      font-size: 12px;
      text-transform: none;
    }

    .panel-body {
      gap: 12px;
      padding: 12px;
    }

    .status-line {
      color: var(--oc-muted);
    }

    .status-line strong {
      color: var(--oc-text);
      font-weight: 610;
    }

    .notice {
      border-color: rgba(214, 184, 106, 0.32);
      border-radius: 6px;
      background: rgba(214, 184, 106, 0.08);
      color: #dec986;
    }

    .drop-zone,
    .region-stage,
    .speech-table-wrap {
      border-color: var(--oc-line-strong);
      border-radius: 8px;
      background: #101010;
    }

    .drop-zone.dragging,
    .region-stage.active {
      border-color: var(--oc-blue);
      background: #111827;
    }

    .drop-title {
      color: var(--oc-text);
      font-weight: 610;
    }

    .region-toolbar {
      align-items: end;
    }

    .region-selection {
      border-color: var(--oc-blue);
      background: rgba(47, 111, 255, 0.14);
      box-shadow: 0 0 0 9999px rgba(0, 0, 0, 0.52);
    }

    .speech-table th,
    .speech-table td {
      border-bottom-color: var(--oc-line);
    }

    .speech-table th {
      color: var(--oc-muted);
      font-weight: 640;
    }

    .footer {
      grid-column: 1 / 5;
      min-height: 28px;
      border-top-color: #1f1f1f;
      background: var(--oc-titlebar);
      color: #828282;
      font-size: 11px;
    }

    .workspace.settings-active {
      grid-column: 3 / 5;
    }

    .settings-pane {
      min-height: 0;
      height: 100%;
      display: grid;
      grid-template-columns: 236px minmax(0, 1fr);
      overflow: hidden;
      border: 1px solid var(--oc-line);
      border-radius: 8px;
      background: #111111;
    }

    .settings-pane.hidden {
      display: none;
    }

    .terminal.hidden,
    .composer.hidden,
    .inspector.hidden {
      display: none;
    }

    .settings-nav {
      display: flex;
      min-width: 0;
      flex-direction: column;
      justify-content: space-between;
      gap: 18px;
      padding: 18px 14px;
      border-right: 1px solid var(--oc-line);
      background: #171717;
    }

    .settings-nav-main {
      display: grid;
      gap: 18px;
      align-content: start;
    }

    .settings-nav-group {
      display: grid;
      gap: 8px;
    }

    .settings-nav-title {
      padding: 0 8px;
      color: #777777;
      font-family: var(--mono);
      font-size: 11px;
      font-weight: 640;
    }

    .settings-nav-button {
      justify-content: flex-start;
      min-height: 32px;
      width: 100%;
      border-color: transparent;
      background: transparent;
      color: #969696;
      font-size: 13px;
    }

    .settings-nav-button:hover,
    .settings-nav-button.active {
      border-color: transparent;
      background: #292929;
      color: #f1f1f1;
    }

    .settings-nav-footer {
      display: grid;
      gap: 8px;
      padding: 0 8px 2px;
      color: var(--oc-faint);
      font-family: var(--mono);
      font-size: 11px;
    }

    .settings-panel {
      min-width: 0;
      min-height: 0;
      overflow: auto;
      scrollbar-width: none;
      background: #111111;
    }

    .settings-panel::-webkit-scrollbar {
      display: none;
    }

    .settings-section-panel {
      min-height: 100%;
    }

    .settings-tab-header {
      position: sticky;
      top: 0;
      z-index: 2;
      padding: 40px 40px 32px;
      background: linear-gradient(to bottom, #111111 calc(100% - 24px), transparent);
    }

    .settings-tab-title {
      margin: 0;
      color: var(--oc-text);
      font-size: 15px;
      font-weight: 640;
      line-height: 1;
    }

    .settings-tab-body {
      display: flex;
      flex-direction: column;
      gap: 36px;
      width: 100%;
      padding: 0 40px 40px;
    }

    .settings-section {
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    .settings-section-title {
      padding-bottom: 8px;
      color: var(--oc-text);
      font-size: 15px;
      font-weight: 640;
      line-height: 1;
    }

    [data-component="settings-v2-list"] {
      border-radius: 8px;
      background-color: #171717;
      padding-inline: 20px;
      box-shadow: inset 0 0 0 1px var(--oc-line);
    }

    [data-component="settings-v2-row"] {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 16px;
      padding-block: 20px;
      border-bottom: 1px solid var(--oc-line);
    }

    [data-component="settings-v2-row"]:last-child {
      border-bottom: none;
    }

    [data-slot="settings-v2-row-copy"] {
      display: flex;
      min-width: 0;
      flex: 1;
      flex-direction: column;
      gap: 8px;
    }

    [data-slot="settings-v2-row-title"] {
      color: var(--oc-text);
      font-size: 13px;
      font-weight: 530;
      line-height: 1;
    }

    [data-slot="settings-v2-row-description"] {
      color: var(--oc-muted);
      font-size: 11px;
      font-weight: 440;
      line-height: 1.35;
    }

    [data-slot="settings-v2-row-control"] {
      display: flex;
      width: 100%;
      justify-content: flex-end;
    }

    .settings-control {
      width: min(340px, 100%);
    }

    .settings-control-popover {
      position: relative;
      width: min(420px, 100%);
    }

    .settings-control-popover .settings-control {
      margin-left: auto;
    }

    .ocr-provider-help {
      position: absolute;
      right: 0;
      bottom: calc(100% + 10px);
      z-index: 5;
      width: min(420px, calc(100vw - 64px));
      padding: 12px;
      border: 1px solid #303030;
      border-radius: 8px;
      background: #202020;
      box-shadow: 0 16px 36px rgba(0, 0, 0, 0.36);
      opacity: 0;
      transform: translateY(4px);
      visibility: hidden;
      pointer-events: none;
      transition: opacity 150ms ease, transform 150ms ease, visibility 150ms ease;
    }

    .settings-control-popover:hover .ocr-provider-help,
    .settings-control-popover:focus-within .ocr-provider-help,
    .settings-control-popover.show-help .ocr-provider-help {
      opacity: 1;
      transform: translateY(0);
      visibility: visible;
      pointer-events: auto;
    }

    .ocr-provider-help-kicker {
      color: var(--oc-faint);
      font-family: var(--mono);
      font-size: 10px;
      line-height: 1;
      text-transform: uppercase;
    }

    .ocr-provider-help-title {
      margin-top: 8px;
      color: var(--oc-text);
      font-size: 13px;
      font-weight: 640;
      line-height: 1.25;
    }

    .ocr-provider-help-value {
      margin-top: 4px;
      color: #8eb4ff;
      font-family: var(--mono);
      font-size: 11px;
      line-height: 1.3;
    }

    .ocr-provider-help-description {
      margin-top: 8px;
      color: var(--oc-muted);
      font-size: 12px;
      line-height: 1.45;
    }

    .settings-control-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px;
      width: min(360px, 100%);
    }

    .settings-status-stack {
      display: grid;
      gap: 8px;
      width: min(360px, 100%);
    }

    .settings-action-row {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      width: min(360px, 100%);
      flex-wrap: wrap;
    }

    .settings-check {
      display: inline-flex;
      align-items: center;
      justify-content: flex-end;
      gap: 10px;
      min-height: 32px;
      color: var(--oc-text);
      font-size: 13px;
    }

    .settings-check input {
      width: 16px;
      min-height: 16px;
      accent-color: var(--oc-blue);
    }

    .settings-license {
      width: min(420px, 100%);
      color: var(--oc-muted);
      font-size: 11px;
      line-height: 1.45;
      text-align: right;
    }

    .settings-license a {
      color: #80a7ff;
      text-decoration: none;
    }

    .settings-license a:hover {
      color: #a8c1ff;
    }

    @media (min-width: 640px) {
      [data-component="settings-v2-row"] {
        flex-wrap: nowrap;
      }

      [data-slot="settings-v2-row-control"] {
        width: auto;
        flex-shrink: 0;
      }
    }

    ::selection {
      background: rgba(47, 111, 255, 0.38);
      color: #ffffff;
    }

    @media (max-width: 1160px) {
      .shell {
        grid-template-columns: 56px 218px minmax(0, 1fr);
      }

      .topbar,
      .footer {
        grid-column: 1 / 4;
      }

      .inspector {
        display: none;
      }
    }

    @media (max-width: 760px) {
      .shell {
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: 32px auto minmax(0, 1fr) auto 28px;
      }

      .topbar,
      .footer {
        grid-column: 1 / 2;
      }

      .activity-rail {
        display: none;
      }

      .sidebar {
        grid-column: 1;
        grid-row: 2 / 3;
        border-right: 0;
      }

      .workspace {
        grid-column: 1;
        grid-row: 3 / 5;
        padding: 14px;
      }

      .workspace.settings-active {
        grid-column: 1;
      }

      .settings-pane {
        grid-template-columns: minmax(0, 1fr);
      }

      .settings-nav {
        border-right: 0;
        border-bottom: 1px solid var(--oc-line);
      }

      .settings-nav-main {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .settings-tab-header {
        padding: 24px 20px 20px;
      }

      .settings-tab-body {
        padding: 0 20px 24px;
      }

      .footer {
        grid-row: 5 / 6;
      }

      .side-section {
        padding: 12px;
      }

      .nav {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .top-actions .pill:nth-child(2) {
        display: none;
      }
    }
  </style>
</head>
<body>
  <div class="shell">
    <header class="topbar">
      <div class="brand"><span class="mark">OC</span><span>YomiBridge</span></div>
      <div class="top-actions">
        <span class="pill"><span id="wsDot" class="dot"></span><span id="wsStatus">broadcast</span></span>
        <span class="pill"><span id="healthStatus">booting</span></span>
      </div>
    </header>

    <aside class="activity-rail" aria-label="Primary navigation">
      <div class="rail-top">
        <div class="rail-badge">LTH</div>
        <a class="rail-link active" href="/app" title="Workbench">app</a>
        <a class="rail-link" href="/viewer" title="Viewer">view</a>
      </div>
      <div class="rail-bottom">
        <a class="rail-link" href="/health" title="Health">api</a>
      </div>
    </aside>

    <aside class="sidebar">
      <div class="side-section">
        <div class="side-title">workspace</div>
        <div class="nav">
          <button id="tabTranslate" class="active" type="button">Translate</button>
          <button id="tabOcr" type="button">OCR</button>
          <button id="tabPipeline" type="button">OCR + Translate</button>
          <button id="tabAudio" type="button">Audio</button>
          <button id="tabAudioPipeline" type="button">Audio + Translate</button>
          <button id="tabRegion" type="button">Region</button>
          <button id="tabSettings" type="button">Settings</button>
        </div>
      </div>
      <div class="side-section">
        <div class="side-title">runtime</div>
        <div class="metric-grid">
          <div class="metric"><span>provider</span><strong id="metricProvider">-</strong></div>
          <div class="metric"><span>model</span><strong id="metricModel">-</strong></div>
          <div class="metric"><span>ocr</span><strong id="metricOcr">-</strong></div>
          <div class="metric"><span>asr</span><strong id="metricAsr">-</strong></div>
          <div class="metric"><span>cache</span><strong id="metricCache">-</strong></div>
        </div>
      </div>
      <div class="session-list" id="sessionList"></div>
    </aside>

    <main class="workspace">
      <section id="terminal" class="terminal" aria-live="polite"></section>

      <section id="composer" class="composer">
        <div class="tabs">
          <button id="modeTranslate" class="active" type="button">text</button>
          <button id="modeOcr" type="button">ocr</button>
          <button id="modePipeline" type="button">pipe</button>
          <button id="modeAudio" type="button">audio</button>
          <button id="modeAudioPipeline" type="button">audio pipe</button>
          <button id="modeRegion" type="button">region</button>
          <button id="modeSettings" type="button">settings</button>
        </div>

        <div id="textPane" class="input-grid">
          <div class="field">
            <label for="sourceText">source</label>
            <textarea id="sourceText">こんにちは、勇者さん。</textarea>
          </div>
          <div class="field">
            <label for="lastOutput">result</label>
            <textarea id="lastOutput" readonly></textarea>
          </div>
        </div>

        <div id="ocrPane" class="input-grid hidden">
          <div class="field">
            <label for="imageBase64">image base64</label>
            <textarea id="imageBase64"></textarea>
          </div>
          <div class="field">
            <label for="imageFile">image file</label>
            <div id="dropZone" class="drop-zone" tabindex="0">
              <input id="imageFile" type="file" accept="image/*">
              <div class="drop-title">Drop, paste, or choose an image</div>
              <div class="field-hint">Image input auto-runs OCR. Edit the text below before translating.</div>
            </div>
            <label for="ocrOutput">ocr text</label>
            <textarea id="ocrOutput"></textarea>
          </div>
        </div>

        <div id="audioPane" class="input-grid hidden">
          <div class="field">
            <label for="audioSourceUrl">audio url</label>
            <input id="audioSourceUrl" autocomplete="off">
            <label for="audioFile">audio file</label>
            <input id="audioFile" type="file" accept="audio/*,video/*">
            <label for="audioBase64">audio base64</label>
            <textarea id="audioBase64"></textarea>
          </div>
          <div class="field">
            <label for="asrOutput">asr text</label>
            <textarea id="asrOutput"></textarea>
            <div class="actions-left">
              <button id="copySrtButton" type="button">Copy SRT</button>
              <button id="copyVttButton" type="button">Copy VTT</button>
            </div>
          </div>
          <div class="speech-table-wrap">
            <table class="speech-table">
              <thead>
                <tr><th>time</th><th>source</th><th>translation</th></tr>
              </thead>
              <tbody id="speechSegmentsTable"></tbody>
            </table>
          </div>
        </div>

        <div id="regionPane" class="region-pane hidden">
          <div class="region-toolbar">
            <button id="startRegionCaptureButton" type="button">Capture Screen</button>
            <button id="snapshotRegionButton" class="primary" type="button">Snapshot Translate</button>
            <button id="toggleRegionLoopButton" type="button">Loop Off</button>
            <button id="stopRegionCaptureButton" type="button">Stop</button>
            <div class="field"><label for="regionInterval">interval ms</label><input id="regionInterval" type="number" min="500" step="250" value="1500"></div>
          </div>
          <div id="regionStage" class="region-stage" tabindex="0">
            <video id="regionVideo" autoplay muted playsinline></video>
            <div id="regionPlaceholder" class="region-placeholder">Capture a screen or window, then drag a box over the dialogue area.</div>
            <div id="regionSelection" class="region-selection hidden"></div>
          </div>
          <div class="region-output-grid">
            <div class="field">
              <label for="regionOcrText">region ocr</label>
              <textarea id="regionOcrText"></textarea>
            </div>
            <div class="field">
              <label for="regionTranslationText">region translation</label>
              <textarea id="regionTranslationText" readonly></textarea>
            </div>
          </div>
        </div>

        <div class="actions">
          <div class="actions-left">
            <button id="runButton" class="primary" type="button"><span class="command-key">&gt;</span><span>Run</span></button>
            <button id="translateOcrTextButton" class="hidden" type="button">Translate OCR Text</button>
            <button id="clearButton" type="button">Clear</button>
          </div>
          <div class="actions-right">
            <span class="pill"><span id="modeLabel">text</span></span>
            <span class="pill"><span id="latencyLabel">0 ms</span></span>
          </div>
        </div>
      </section>

      <section id="settingsPane" class="settings-pane hidden" aria-label="Settings">
        <nav class="settings-nav" aria-label="Settings sections">
          <div class="settings-nav-main">
            <div class="settings-nav-group">
              <div class="settings-nav-title">desktop</div>
              <button class="settings-nav-button active" type="button" data-settings-section="general">General</button>
              <button class="settings-nav-button" type="button" data-settings-section="providers">Providers</button>
              <button class="settings-nav-button" type="button" data-settings-section="sound">Sound</button>
            </div>
            <div class="settings-nav-group">
              <div class="settings-nav-title">pipelines</div>
              <button class="settings-nav-button" type="button" data-settings-section="ocr">OCR</button>
              <button class="settings-nav-button" type="button" data-settings-section="audio">Audio</button>
              <button class="settings-nav-button" type="button" data-settings-section="region">Region</button>
            </div>
            <div class="settings-nav-group">
              <div class="settings-nav-title">runtime</div>
              <button class="settings-nav-button" type="button" data-settings-section="broadcast">Broadcast</button>
            </div>
          </div>
          <div class="settings-nav-footer">
            <span>YomiBridge</span>
            <span>OpenCode desktop</span>
          </div>
        </nav>

        <div class="settings-panel">
          <section class="settings-section-panel" data-settings-panel="general">
            <header class="settings-tab-header">
              <h1 class="settings-tab-title">General</h1>
            </header>
            <div class="settings-tab-body">
              <section class="settings-section">
                <div class="settings-section-title">Translation defaults</div>
                <div data-component="settings-v2-list">
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Language pair</div>
                      <div data-slot="settings-v2-row-description">Default source and target for text, OCR, audio, and region pipelines.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control-grid">
                        <div class="field"><label for="source">source</label><input id="source" value="ja"></div>
                        <div class="field"><label for="target">target</label><input id="target" value="zh-TW"></div>
                      </div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Prompt mode</div>
                      <div data-slot="settings-v2-row-description">Prompt preset used when the translation request does not override mode.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control field"><label for="preset">mode</label><select id="preset"></select></div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Glossary</div>
                      <div data-slot="settings-v2-row-description">Optional local glossary file applied to translation requests.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control field"><label for="glossary">glossary</label><select id="glossary"></select></div>
                    </div>
                  </div>
                </div>
              </section>
            </div>
          </section>

          <section class="settings-section-panel hidden" data-settings-panel="providers">
            <header class="settings-tab-header">
              <h1 class="settings-tab-title">Providers</h1>
            </header>
            <div class="settings-tab-body">
              <section class="settings-section">
                <div class="settings-section-title">Translation provider</div>
                <div data-component="settings-v2-list">
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Provider</div>
                      <div data-slot="settings-v2-row-description">Runtime used by text, OCR translate, audio translate, and region translate.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control field"><label for="translationProvider">provider</label><select id="translationProvider"></select></div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Model</div>
                      <div data-slot="settings-v2-row-description">Installed or configured model for the selected provider.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control field"><label for="translationModel">model</label><input id="translationModel" list="translationModelList" autocomplete="off"><datalist id="translationModelList"></datalist></div>
                    </div>
                  </div>
                </div>
              </section>
            </div>
          </section>

          <section class="settings-section-panel hidden" data-settings-panel="sound">
            <header class="settings-tab-header">
              <h1 class="settings-tab-title">Sound</h1>
            </header>
            <div class="settings-tab-body">
              <section class="settings-section">
                <div class="settings-section-title">Sound effects</div>
                <div data-component="settings-v2-list">
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Enable sounds</div>
                      <div data-slot="settings-v2-row-description">Play OpenCode-style feedback sounds for clicks, success, errors, and notifications.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <label class="settings-check" for="soundEnabled"><input id="soundEnabled" type="checkbox">Enabled</label>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Sound mapping</div>
                      <div data-slot="settings-v2-row-description">Choose which local OpenCode sound plays for each UI event.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control-grid">
                        <div class="field"><label for="soundClick">click</label><select id="soundClick"></select></div>
                        <div class="field"><label for="soundSuccess">success</label><select id="soundSuccess"></select></div>
                        <div class="field"><label for="soundError">error</label><select id="soundError"></select></div>
                        <div class="field"><label for="soundNotification">notification</label><select id="soundNotification"></select></div>
                      </div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Preview</div>
                      <div data-slot="settings-v2-row-description">Test the configured sounds without running a translation.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-action-row">
                        <button id="soundTestClickButton" type="button">Click</button>
                        <button id="soundTestSuccessButton" class="primary" type="button">Success</button>
                        <button id="soundTestErrorButton" class="danger" type="button">Error</button>
                      </div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Source</div>
                      <div data-slot="settings-v2-row-description">Audio files are copied locally from OpenCode's MIT-licensed UI package.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-license">
                        <a href="/vendor/opencode/audio/THIRD_PARTY_NOTICES.md" target="_blank" rel="noreferrer">OpenCode audio notice</a>
                      </div>
                    </div>
                  </div>
                </div>
              </section>
            </div>
          </section>

          <section class="settings-section-panel hidden" data-settings-panel="ocr">
            <header class="settings-tab-header">
              <h1 class="settings-tab-title">OCR</h1>
            </header>
            <div class="settings-tab-body">
              <section class="settings-section">
                <div class="settings-section-title">Recognition</div>
                <div data-component="settings-v2-list">
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Engine</div>
                      <div data-slot="settings-v2-row-description">OCR provider used for image and region recognition.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control-popover ocr-provider-control">
                        <div id="ocrProviderHelp" class="ocr-provider-help" role="status" aria-live="polite">
                          <div class="ocr-provider-help-kicker">OCR provider</div>
                          <div id="ocrProviderHelpName" class="ocr-provider-help-title">Select an OCR engine</div>
                          <div id="ocrProviderHelpValue" class="ocr-provider-help-value">Provider value: -</div>
                          <div id="ocrProviderHelpUse" class="ocr-provider-help-description">Hover or focus the OCR engine selector to see what each provider is best at.</div>
                        </div>
                        <div class="settings-control field"><label for="ocrProvider">engine</label><select id="ocrProvider" aria-describedby="ocrProviderHelpUse"></select></div>
                      </div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Language</div>
                      <div data-slot="settings-v2-row-description">Language passed to the OCR provider.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control field"><label for="ocrLanguage">language</label><input id="ocrLanguage" value="ja"></div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Engine status</div>
                      <div data-slot="settings-v2-row-description">Availability and last OCR result metadata.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-status-stack">
                        <div id="ocrHint" class="notice"></div>
                        <div class="status-line"><span>available</span><strong id="ocrEngineAvailability">-</strong></div>
                        <div class="status-line"><span>status</span><strong id="ocrEngineStatus">-</strong></div>
                        <div class="status-line"><span>source</span><strong id="ocrEngineSource">-</strong></div>
                        <div class="status-line"><span>blocks</span><strong id="blockCount">0</strong></div>
                        <div class="status-line"><span>engine</span><strong id="engineName">-</strong></div>
                      </div>
                    </div>
                  </div>
                </div>
              </section>
            </div>
          </section>

          <section class="settings-section-panel hidden" data-settings-panel="audio">
            <header class="settings-tab-header">
              <h1 class="settings-tab-title">Audio</h1>
            </header>
            <div class="settings-tab-body">
              <section class="settings-section">
                <div class="settings-section-title">Speech recognition</div>
                <div data-component="settings-v2-list">
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Engine</div>
                      <div data-slot="settings-v2-row-description">ASR provider used for uploaded audio, video, and source URLs.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control field"><label for="speechProvider">engine</label><select id="speechProvider"></select></div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Language</div>
                      <div data-slot="settings-v2-row-description">Language passed to the speech provider.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control field"><label for="speechLanguage">language</label><input id="speechLanguage" value="ja"></div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Engine status</div>
                      <div data-slot="settings-v2-row-description">Availability and last speech result metadata.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-status-stack">
                        <div id="speechHint" class="notice"></div>
                        <div class="status-line"><span>available</span><strong id="speechEngineAvailability">-</strong></div>
                        <div class="status-line"><span>source</span><strong id="speechEngineSource">-</strong></div>
                        <div class="status-line"><span>segments</span><strong id="speechSegmentCount">0</strong></div>
                        <div class="status-line"><span>engine</span><strong id="speechEngineName">-</strong></div>
                      </div>
                    </div>
                  </div>
                </div>
              </section>
            </div>
          </section>

          <section class="settings-section-panel hidden" data-settings-panel="region">
            <header class="settings-tab-header">
              <h1 class="settings-tab-title">Region</h1>
            </header>
            <div class="settings-tab-body">
              <section class="settings-section">
                <div class="settings-section-title">Screen capture</div>
                <div data-component="settings-v2-list">
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Capture controls</div>
                      <div data-slot="settings-v2-row-description">Start capture, run one snapshot, or toggle region loop.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-action-row">
                        <button id="settingsCaptureButton" type="button">Capture</button>
                        <button id="settingsSnapshotButton" class="primary" type="button">Snapshot</button>
                        <button id="settingsToggleRegionLoopButton" type="button">Loop Off</button>
                      </div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Loop interval</div>
                      <div data-slot="settings-v2-row-description">Milliseconds between automatic region snapshots.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-control field"><label for="regionIntervalSetting">interval ms</label><input id="regionIntervalSetting" type="number" min="500" step="250" value="1500"></div>
                    </div>
                  </div>
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Capture status</div>
                      <div data-slot="settings-v2-row-description">Current screen capture and selected region state.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-status-stack">
                        <div class="status-line"><span>capture</span><strong id="regionCaptureStatus">off</strong></div>
                        <div class="status-line"><span>loop</span><strong id="regionLoopStatus">off</strong></div>
                        <div class="status-line"><span>selection</span><strong id="regionSelectionStatus">-</strong></div>
                      </div>
                    </div>
                  </div>
                </div>
              </section>
            </div>
          </section>

          <section class="settings-section-panel hidden" data-settings-panel="broadcast">
            <header class="settings-tab-header">
              <h1 class="settings-tab-title">Broadcast</h1>
            </header>
            <div class="settings-tab-body">
              <section class="settings-section">
                <div class="settings-section-title">Latest translation</div>
                <div data-component="settings-v2-list">
                  <div data-component="settings-v2-row">
                    <div data-slot="settings-v2-row-copy">
                      <div data-slot="settings-v2-row-title">Broadcast payload</div>
                      <div data-slot="settings-v2-row-description">Latest successful translation sent to the viewer.</div>
                    </div>
                    <div data-slot="settings-v2-row-control">
                      <div class="settings-status-stack">
                        <div class="status-line"><span>source</span><strong id="latestSource">-</strong></div>
                        <div class="status-line"><span>target</span><strong id="latestTarget">-</strong></div>
                        <div class="status-line"><span>provider</span><strong id="latestProvider">-</strong></div>
                      </div>
                    </div>
                  </div>
                </div>
              </section>
            </div>
          </section>
        </div>
      </section>
    </main>

    <aside class="inspector">
      <section class="panel">
        <div class="panel-title">desktop</div>
        <div class="panel-body">
          <div class="status-line"><span>surface</span><strong>workbench</strong></div>
          <div class="status-line"><span>style</span><strong>OpenCode</strong></div>
          <div class="status-line"><span>settings</span><strong>row list</strong></div>
        </div>
      </section>

      <section class="panel">
        <div class="panel-title">routes</div>
        <div class="panel-body">
          <div class="status-line"><span>app</span><strong>/app</strong></div>
          <div class="status-line"><span>viewer</span><strong>/viewer</strong></div>
          <div class="status-line"><span>api</span><strong>/health</strong></div>
        </div>
      </section>
    </aside>

    <footer class="footer">
      <span id="footerLeft">ready</span>
      <span id="footerRight">/app</span>
    </footer>
  </div>

  <script>
    const $ = id => document.getElementById(id);
    const state = {
      mode: "text",
      history: [],
      latestOcrText: "",
      latestSpeechSegments: [],
      latestSpeechTranslations: [],
      audioMimeType: "",
      regionStream: null,
      regionSelection: null,
      regionDrag: null,
      regionLoopRunning: false,
      regionLoopTimer: null,
      busy: false
    };

    const terminal = $("terminal");
    const sourceText = $("sourceText");
    const lastOutput = $("lastOutput");
    const imageBase64 = $("imageBase64");
    const imageFile = $("imageFile");
    const ocrOutput = $("ocrOutput");
    const dropZone = $("dropZone");
    const audioBase64 = $("audioBase64");
    const audioFile = $("audioFile");
    const audioSourceUrl = $("audioSourceUrl");
    const asrOutput = $("asrOutput");
    const speechSegmentsTable = $("speechSegmentsTable");
    const regionVideo = $("regionVideo");
    const regionStage = $("regionStage");
    const regionSelection = $("regionSelection");
    const regionOcrText = $("regionOcrText");
    const regionTranslationText = $("regionTranslationText");
    const soundIds = [
      "alert-01",
      "alert-02",
      "alert-03",
      "alert-04",
      "alert-05",
      "alert-06",
      "alert-07",
      "alert-08",
      "alert-09",
      "alert-10",
      "bip-bop-01",
      "bip-bop-02",
      "bip-bop-03",
      "bip-bop-04",
      "bip-bop-05",
      "bip-bop-06",
      "bip-bop-07",
      "bip-bop-08",
      "bip-bop-09",
      "bip-bop-10",
      "nope-01",
      "nope-02",
      "nope-03",
      "nope-04",
      "nope-05",
      "nope-06",
      "nope-07",
      "nope-08",
      "nope-09",
      "nope-10",
      "nope-11",
      "nope-12",
      "staplebops-01",
      "staplebops-02",
      "staplebops-03",
      "staplebops-04",
      "staplebops-05",
      "staplebops-06",
      "staplebops-07",
      "yup-01",
      "yup-02",
      "yup-03",
      "yup-04",
      "yup-05",
      "yup-06"
    ];
    const soundFiles = Object.fromEntries(
      soundIds.map(id => [id, `/vendor/opencode/audio/${id}.mp3`])
    );
    const soundOptions = [["", "none"], ...soundIds.map(id => [id, id])];
    const defaultSoundSettings = {
      enabled: true,
      click: "bip-bop-01",
      success: "yup-01",
      error: "nope-01",
      notification: "alert-01"
    };
    const soundSettings = loadSoundSettings();
    const ocrProviderDescriptions = {
      external: {
        name: "Windows OCR",
        value: "external",
        use: "預設本機 OCR，輕量，適合一般文字"
      },
      tesseract: {
        name: "Tesseract OCR",
        value: "tesseract",
        use: "離線文字 OCR，已裝 eng/jpn/chi_tra/chi_sim"
      },
      easyocr: {
        name: "EasyOCR",
        value: "easyocr",
        use: "本地多語 OCR，適合一般圖片文字"
      },
      paddleocr: {
        name: "PaddleOCR / PP-OCR",
        value: "paddleocr",
        use: "CJK 文字 OCR，適合中日文一般文字"
      },
      "pp-structure-v3": {
        name: "PP-StructureV3",
        value: "pp-structure-v3",
        use: "表格、版面、公式等結構 OCR"
      },
      "paddleocr-vl": {
        name: "PaddleOCR-VL",
        value: "paddleocr-vl",
        use: "高精度文件/表格/公式 OCR，已下載模型，但很慢"
      },
      pix2text: {
        name: "Pix2Text",
        value: "pix2text",
        use: "數學式、表格、markdown 式文件抽取"
      }
    };
    let ocrProviderHelpTimer = 0;

    function loadSoundSettings() {
      try {
        const raw = window.localStorage.getItem("lth.soundSettings");
        return { ...defaultSoundSettings, ...(raw ? JSON.parse(raw) : {}) };
      } catch {
        return { ...defaultSoundSettings };
      }
    }

    function saveSoundSettings() {
      try {
        window.localStorage.setItem("lth.soundSettings", JSON.stringify(soundSettings));
      } catch {
      }
    }

    function populateSoundSelect(select, selected) {
      select.innerHTML = "";
      for (const [value, label] of soundOptions) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = label;
        select.appendChild(option);
      }

      select.value = selected in soundFiles || selected === "" ? selected : "";
    }

    function syncSoundControls() {
      $("soundEnabled").checked = Boolean(soundSettings.enabled);
      populateSoundSelect($("soundClick"), soundSettings.click);
      populateSoundSelect($("soundSuccess"), soundSettings.success);
      populateSoundSelect($("soundError"), soundSettings.error);
      populateSoundSelect($("soundNotification"), soundSettings.notification);
    }

    function readSoundControls() {
      soundSettings.enabled = $("soundEnabled").checked;
      soundSettings.click = $("soundClick").value;
      soundSettings.success = $("soundSuccess").value;
      soundSettings.error = $("soundError").value;
      soundSettings.notification = $("soundNotification").value;
      saveSoundSettings();
    }

    function playUiSound(kind, force = false) {
      if (!force && !soundSettings.enabled) {
        return;
      }

      const id = soundSettings[kind] || "";
      const src = soundFiles[id];
      if (!src || typeof Audio === "undefined") {
        return;
      }

      const audio = new Audio(src);
      audio.volume = 0.62;
      audio.play().catch(() => undefined);
    }

    function setBusy(value) {
      state.busy = value;
      $("runButton").disabled = value;
      $("footerLeft").textContent = value ? "running" : "ready";
    }

    async function api(path, options = {}) {
      const response = await fetch(path, {
        headers: { "content-type": "application/json" },
        ...options
      });
      const text = await response.text();
      let data = null;
      if (text) {
        try {
          data = JSON.parse(text);
        } catch {
          data = text;
        }
      }
      if (!response.ok) {
        const message = data && data.errorMessage ? data.errorMessage : text || response.statusText;
        throw new Error(message);
      }
      return data;
    }

    function post(path, body) {
      return api(path, { method: "POST", body: JSON.stringify(body) });
    }

    function populateSelect(select, items, getValue, getLabel, fallback) {
      select.innerHTML = "";
      for (const item of items) {
        const option = document.createElement("option");
        option.value = getValue(item);
        option.textContent = getLabel(item);
        select.appendChild(option);
      }
      if (fallback && Array.from(select.options).some(option => option.value === fallback)) {
        select.value = fallback;
      }
    }

    function populateModelList(models, fallback) {
      const input = $("translationModel");
      const list = $("translationModelList");
      list.innerHTML = "";

      for (const item of models || []) {
        const option = document.createElement("option");
        option.value = item.name;
        option.label = item.isInstalled ? item.name : `${item.name} (${item.source || "configured"})`;
        list.appendChild(option);
      }

      const names = Array.from(list.options).map(option => option.value);
      const defaultModel = (models || []).find(item => item.isDefault)?.name || fallback || names[0] || "";
      if (!input.value || (names.length > 0 && !names.includes(input.value))) {
        input.value = defaultModel;
      }

      $("metricModel").textContent = input.value || "-";
    }

    function getOcrProviderDescription(value, displayName, note) {
      const key = String(value || "").toLowerCase();
      const description = ocrProviderDescriptions[key];
      if (description) {
        return description;
      }

      return {
        name: displayName || value || "OCR provider",
        value: value || "-",
        use: note || "selected OCR provider will process the uploaded image."
      };
    }

    function formatOcrProviderDescription(description) {
      return `${description.name} | Provider 值: ${description.value} | ${description.use}`;
    }

    function updateOcrProviderHelp(description) {
      $("ocrProviderHelpName").textContent = description.name;
      $("ocrProviderHelpValue").textContent = `Provider 值: ${description.value}`;
      $("ocrProviderHelpUse").textContent = description.use;
      $("ocrProvider").title = formatOcrProviderDescription(description);
    }

    function pulseOcrProviderHelp() {
      const control = document.querySelector(".ocr-provider-control");
      if (!control) {
        return;
      }

      window.clearTimeout(ocrProviderHelpTimer);
      control.classList.add("show-help");
      ocrProviderHelpTimer = window.setTimeout(() => control.classList.remove("show-help"), 1800);
    }

    function populateOcrEngines(engines, fallback) {
      const select = $("ocrProvider");
      select.innerHTML = "";

      for (const item of engines || []) {
        const option = document.createElement("option");
        option.value = item.name;
        const statusLabel = item.status === "requires_api_configuration"
          ? "api config"
          : item.status === "planned_structure"
            ? "structure"
            : item.status === "missing_dependency"
              ? "missing deps"
          : item.status === "planned_local"
            ? "planned local"
            : item.status || "unavailable";
        option.textContent = item.isAvailable ? item.displayName : `${item.displayName} (${statusLabel})`;
        option.disabled = !item.isAvailable;
        const description = getOcrProviderDescription(item.name, item.displayName, item.note);
        option.title = formatOcrProviderDescription(description);
        option.dataset.note = item.note || "";
        option.dataset.source = item.source || item.kind || "";
        option.dataset.status = item.status || "";
        option.dataset.available = item.isAvailable ? "true" : "false";
        option.dataset.defaultLanguage = item.defaultLanguage || "";
        option.dataset.helpName = description.name;
        option.dataset.helpValue = description.value;
        option.dataset.helpUse = description.use;
        select.appendChild(option);
      }

      const options = Array.from(select.options);
      const fallbackOption = options.find(option => option.value === fallback && !option.disabled);
      const defaultOption = options.find(option => !option.disabled && (engines || []).find(item => item.name === option.value && item.isDefault));
      const firstAvailable = options.find(option => !option.disabled);
      select.value = (fallbackOption || defaultOption || firstAvailable || options[0])?.value || "";
      syncOcrHint();
    }

    function populateSpeechEngines(engines, fallback) {
      const select = $("speechProvider");
      select.innerHTML = "";

      for (const item of engines || []) {
        const option = document.createElement("option");
        option.value = item.name;
        option.textContent = item.isAvailable ? item.displayName : `${item.displayName} (unavailable)`;
        option.disabled = !item.isAvailable;
        option.dataset.note = item.note || "";
        option.dataset.source = item.source || item.kind || "";
        option.dataset.available = item.isAvailable ? "true" : "false";
        option.dataset.defaultLanguage = item.defaultLanguage || "";
        select.appendChild(option);
      }

      const options = Array.from(select.options);
      const fallbackOption = options.find(option => option.value === fallback && !option.disabled);
      const defaultOption = options.find(option => !option.disabled && (engines || []).find(item => item.name === option.value && item.isDefault));
      const firstAvailable = options.find(option => !option.disabled);
      select.value = (fallbackOption || defaultOption || firstAvailable || options[0])?.value || "";
      syncSpeechHint();
    }

    async function loadTranslationModels() {
      const provider = $("translationProvider").value;
      try {
        const models = await api(`/translation/models?provider=${encodeURIComponent(provider)}`);
        populateModelList(models || [], $("translationModel").value);
      } catch (error) {
        $("metricModel").textContent = $("translationModel").value || "-";
        appendMessage("error", "models", error.message || String(error));
      }
    }

    function appendMessage(kind, title, body, meta = "") {
      const wrap = document.createElement("article");
      wrap.className = `message ${kind}`;

      const head = document.createElement("div");
      head.className = "message-head";

      const left = document.createElement("span");
      left.textContent = title;

      const right = document.createElement("span");
      right.textContent = meta || new Date().toLocaleTimeString();

      const content = document.createElement("div");
      content.className = "message-body";
      content.textContent = body || "";

      head.append(left, right);
      wrap.append(head, content);
      terminal.appendChild(wrap);
      terminal.scrollTop = terminal.scrollHeight;

      state.history.unshift({ title, body, meta });
      state.history = state.history.slice(0, 8);
      renderSessions();
      if (kind === "error") {
        playUiSound("error");
      } else if (kind === "result") {
        playUiSound("success");
      }
    }

    function renderSessions() {
      const list = $("sessionList");
      list.innerHTML = "";
      for (const item of state.history) {
        const node = document.createElement("div");
        node.className = "session-item";
        const title = document.createElement("strong");
        title.textContent = item.title;
        const body = document.createElement("span");
        body.textContent = item.body || item.meta || "";
        node.append(title, body);
        list.appendChild(node);
      }
    }

    function setMode(mode) {
      state.mode = mode;
      const isText = mode === "text";
      const isOcr = mode === "ocr";
      const isAudio = mode === "audio";
      const isRegion = mode === "region";
      const isSettings = mode === "settings";
      $("textPane").classList.toggle("hidden", !isText);
      $("ocrPane").classList.toggle("hidden", !isOcr && mode !== "pipe");
      $("audioPane").classList.toggle("hidden", !isAudio && mode !== "audioPipe");
      $("regionPane").classList.toggle("hidden", !isRegion);
      $("settingsPane").classList.toggle("hidden", !isSettings);
      $("terminal").classList.toggle("hidden", isSettings);
      $("composer").classList.toggle("hidden", isSettings);
      document.querySelector(".workspace").classList.toggle("settings-active", isSettings);
      document.querySelector(".inspector").classList.toggle("hidden", isSettings);
      $("translateOcrTextButton").classList.toggle("hidden", !isOcr);
      $("modeLabel").textContent = mode;
      $("footerRight").textContent = isSettings ? "/app/settings" : "/app";

      const pairs = [
        ["tabTranslate", "modeTranslate", "text"],
        ["tabOcr", "modeOcr", "ocr"],
        ["tabPipeline", "modePipeline", "pipe"],
        ["tabAudio", "modeAudio", "audio"],
        ["tabAudioPipeline", "modeAudioPipeline", "audioPipe"],
        ["tabRegion", "modeRegion", "region"],
        ["tabSettings", "modeSettings", "settings"]
      ];
      for (const [navId, tabId, value] of pairs) {
        $(navId).classList.toggle("active", mode === value);
        $(tabId).classList.toggle("active", mode === value);
      }
    }

    function setSettingsSection(section) {
      for (const button of document.querySelectorAll("[data-settings-section]")) {
        button.classList.toggle("active", button.dataset.settingsSection === section);
      }

      for (const panel of document.querySelectorAll("[data-settings-panel]")) {
        panel.classList.toggle("hidden", panel.dataset.settingsPanel !== section);
      }
    }

    function syncOcrHint() {
      const provider = $("ocrProvider").value;
      const hint = $("ocrHint");
      const selected = $("ocrProvider").selectedOptions[0];
      const description = selected
        ? {
            name: selected.dataset.helpName || selected.textContent || provider || "OCR provider",
            value: selected.dataset.helpValue || provider || "-",
            use: selected.dataset.helpUse || selected.dataset.note || "selected OCR provider will process the uploaded image."
          }
        : getOcrProviderDescription(provider, "", "");
      updateOcrProviderHelp(description);
      $("ocrEngineAvailability").textContent = selected && selected.dataset.available === "true" ? "yes" : "no";
      $("ocrEngineStatus").textContent = selected && selected.dataset.status ? selected.dataset.status : "-";
      $("ocrEngineSource").textContent = selected && selected.dataset.source ? selected.dataset.source : "-";
      if (description && description.use) {
        hint.textContent = description.use;
      } else if (selected && selected.dataset.note) {
        hint.textContent = selected.dataset.note;
      } else {
        hint.textContent = "selected OCR provider will process the uploaded image.";
      }

      if (selected && selected.dataset.defaultLanguage && !$("ocrLanguage").value.trim()) {
        $("ocrLanguage").value = selected.dataset.defaultLanguage;
      }
    }

    function syncSpeechHint() {
      const selected = $("speechProvider").selectedOptions[0];
      $("speechEngineAvailability").textContent = selected && selected.dataset.available === "true" ? "yes" : "no";
      $("speechEngineSource").textContent = selected && selected.dataset.source ? selected.dataset.source : "-";
      $("speechHint").textContent = selected && selected.dataset.note
        ? selected.dataset.note
        : "selected ASR provider will process the uploaded audio.";
      if (selected && selected.dataset.defaultLanguage && !$("speechLanguage").value.trim()) {
        $("speechLanguage").value = selected.dataset.defaultLanguage;
      }
    }

    function translationPayload(text) {
      const glossary = $("glossary").value;
      return {
        text,
        source: $("source").value,
        target: $("target").value,
        mode: $("preset").value,
        provider: $("translationProvider").value,
        model: $("translationModel").value.trim() || null,
        glossary: glossary || null
      };
    }

    function speechPayload() {
      const glossary = $("glossary").value;
      const sourceUrl = audioSourceUrl.value.trim();
      return {
        audioBase64: sourceUrl ? null : audioBase64.value.trim(),
        audioMimeType: state.audioMimeType || null,
        sourceUrl: sourceUrl || null,
        provider: $("speechProvider").value,
        language: $("speechLanguage").value,
        glossary: glossary || null
      };
    }

    function ocrPayload() {
      return {
        imageBase64: imageBase64.value.trim(),
        provider: $("ocrProvider").value,
        language: $("ocrLanguage").value
      };
    }

    async function translateText(text, title = "source") {
      if (!text) {
        appendMessage("error", "translate", "source is empty");
        return null;
      }

      const model = $("translationModel").value.trim();
      const provider = $("translationProvider").value;
      appendMessage("user", title, text, model ? `${provider} / ${model}` : provider);
      const started = performance.now();
      const result = await post("/translate", translationPayload(text));
      const elapsed = Math.round(performance.now() - started);
      $("latencyLabel").textContent = `${elapsed} ms`;

      if (result.errorCode !== "0") {
        appendMessage("error", "translate", result.errorMessage || result.result, `${elapsed} ms`);
        return;
      }

      lastOutput.value = result.result || "";
      appendMessage("result", "translation", result.result || "", `${elapsed} ms`);
      $("metricCache").textContent = "ok";
      return result;
    }

    async function runTranslate() {
      const text = sourceText.value.trim();
      await translateText(text, "source");
    }

    async function translateEditedOcrText() {
      if (state.busy) {
        return;
      }

      const text = ocrOutput.value.trim();
      if (!text) {
        appendMessage("error", "translation", "ocr text is empty");
        return;
      }

      setBusy(true);
      try {
        sourceText.value = text;
        await translateText(text, "edited ocr");
      } catch (error) {
        appendMessage("error", "translation", error.message || String(error));
      } finally {
        setBusy(false);
      }
    }

    async function runOcr() {
      if (!imageBase64.value.trim()) {
        appendMessage("error", "ocr", "image base64 is empty");
        return null;
      }

      const started = performance.now();
      const result = await post("/ocr", ocrPayload());
      const elapsed = Math.round(performance.now() - started);
      $("latencyLabel").textContent = `${elapsed} ms`;

      state.latestOcrText = result.text || "";
      ocrOutput.value = state.latestOcrText;
      sourceText.value = state.latestOcrText;
      $("blockCount").textContent = String(result.blocks ? result.blocks.length : 0);
      $("engineName").textContent = result.engine || "-";
      appendMessage("result", "ocr", state.latestOcrText, `${result.provider} ${elapsed} ms`);
      return result;
    }

    function buildOcrTranslatePayload(imageBase64Value, imageMimeType = null) {
      const glossary = $("glossary").value;
      return {
        imageBase64: imageBase64Value,
        ...(imageMimeType ? { imageMimeType } : {}),
        ocrProvider: $("ocrProvider").value,
        language: $("ocrLanguage").value,
        translationProvider: $("translationProvider").value,
        model: $("translationModel").value.trim() || null,
        source: $("source").value,
        target: $("target").value,
        mode: $("preset").value,
        glossary: glossary || null
      };
    }

    async function runPipeline() {
      if (!imageBase64.value.trim()) {
        appendMessage("error", "pipe", "image base64 is empty");
        return;
      }

      const started = performance.now();
      const result = await post("/ocr/translate", buildOcrTranslatePayload(imageBase64.value.trim()));
      const elapsed = Math.round(performance.now() - started);
      $("latencyLabel").textContent = `${elapsed} ms`;

      state.latestOcrText = result.ocr.text || "";
      ocrOutput.value = state.latestOcrText;
      sourceText.value = state.latestOcrText;
      $("blockCount").textContent = String(result.ocr.blocks ? result.ocr.blocks.length : 0);
      $("engineName").textContent = result.ocr.engine || "-";

      const translation = result.translation || {};
      lastOutput.value = translation.result || "";
      appendMessage("user", "ocr", state.latestOcrText, result.ocr.provider || "");
      if (translation.errorCode && translation.errorCode !== "0") {
        appendMessage("error", "translation", translation.errorMessage || translation.result || "", `${elapsed} ms`);
      } else {
        appendMessage("result", "translation", translation.result || "", `${elapsed} ms`);
      }
    }

    function renderSpeechSegments(speech, translations = []) {
      const byIndex = new Map((translations || []).map(item => [item.index, item]));
      const segments = speech && speech.segments ? speech.segments : [];
      state.latestSpeechSegments = segments;
      state.latestSpeechTranslations = translations || [];
      speechSegmentsTable.innerHTML = "";

      for (const segment of segments) {
        const row = document.createElement("tr");
        const time = document.createElement("td");
        const source = document.createElement("td");
        const translationCell = document.createElement("td");
        const translation = byIndex.get(segment.index);
        time.textContent = `${formatClock(segment.startSeconds)}-${formatClock(segment.endSeconds)}`;
        source.textContent = segment.text || "";
        translationCell.textContent = translation ? translation.translatedText || translation.errorMessage || "" : "";
        row.append(time, source, translationCell);
        speechSegmentsTable.appendChild(row);
      }

      const text = speech && speech.text ? speech.text : segments.map(segment => segment.text || "").join("\n");
      asrOutput.value = text;
      sourceText.value = text;
      $("speechSegmentCount").textContent = String(segments.length);
      $("speechEngineName").textContent = speech && speech.engine ? speech.engine : "-";
      if (translations && translations.length > 0) {
        lastOutput.value = translations.map(item => item.translatedText || "").filter(Boolean).join("\n");
      }
    }

    async function runAsr() {
      if (!audioSourceUrl.value.trim() && !audioBase64.value.trim()) {
        appendMessage("error", "asr", "audio input is empty");
        return null;
      }

      const started = performance.now();
      const result = await post("/asr", speechPayload());
      const elapsed = Math.round(performance.now() - started);
      $("latencyLabel").textContent = `${elapsed} ms`;
      renderSpeechSegments(result, []);
      appendMessage("result", "asr", result.text || "", `${result.provider} ${elapsed} ms`);
      return result;
    }

    async function runAsrPipeline() {
      if (!audioSourceUrl.value.trim() && !audioBase64.value.trim()) {
        appendMessage("error", "audio pipe", "audio input is empty");
        return;
      }

      const started = performance.now();
      const glossary = $("glossary").value;
      const sourceUrl = audioSourceUrl.value.trim();
      const result = await post("/asr/translate", {
        audioBase64: sourceUrl ? null : audioBase64.value.trim(),
        audioMimeType: state.audioMimeType || null,
        sourceUrl: sourceUrl || null,
        speechProvider: $("speechProvider").value,
        language: $("speechLanguage").value,
        translationProvider: $("translationProvider").value,
        model: $("translationModel").value.trim() || null,
        source: $("source").value || $("speechLanguage").value,
        target: $("target").value,
        mode: $("preset").value || "subtitle",
        glossary: glossary || null
      });
      const elapsed = Math.round(performance.now() - started);
      $("latencyLabel").textContent = `${elapsed} ms`;
      renderSpeechSegments(result.speech, result.translations || []);
      appendMessage("user", "asr", result.speech && result.speech.text ? result.speech.text : "", result.speech ? result.speech.provider : "");
      appendMessage("result", "audio translation", lastOutput.value, `${elapsed} ms`);
    }

    function formatClock(seconds) {
      const value = Math.max(0, Number(seconds) || 0);
      const whole = Math.floor(value);
      const minutes = Math.floor(whole / 60);
      const remain = whole % 60;
      return `${String(minutes).padStart(2, "0")}:${String(remain).padStart(2, "0")}`;
    }

    function formatSubtitleTime(seconds, separator) {
      const value = Math.max(0, Number(seconds) || 0);
      const whole = Math.floor(value);
      const millis = Math.round((value - whole) * 1000);
      const hours = Math.floor(whole / 3600);
      const minutes = Math.floor((whole % 3600) / 60);
      const remain = whole % 60;
      return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(remain).padStart(2, "0")}${separator}${String(millis).padStart(3, "0")}`;
    }

    function currentSubtitleRows() {
      const byIndex = new Map((state.latestSpeechTranslations || []).map(item => [item.index, item]));
      return (state.latestSpeechSegments || []).map(segment => {
        const translation = byIndex.get(segment.index);
        return {
          index: segment.index,
          start: segment.startSeconds,
          end: segment.endSeconds,
          text: translation && translation.translatedText ? translation.translatedText : segment.text || ""
        };
      }).filter(row => row.text);
    }

    function buildSrt() {
      return currentSubtitleRows().map((row, index) =>
        `${index + 1}\n${formatSubtitleTime(row.start, ",")} --> ${formatSubtitleTime(row.end, ",")}\n${row.text}\n`).join("\n");
    }

    function buildVtt() {
      return `WEBVTT\n\n${currentSubtitleRows().map(row =>
        `${formatSubtitleTime(row.start, ".")} --> ${formatSubtitleTime(row.end, ".")}\n${row.text}\n`).join("\n")}`;
    }

    async function copySubtitle(kind) {
      const text = kind === "vtt" ? buildVtt() : buildSrt();
      if (!text.trim()) {
        appendMessage("error", "subtitle", "no segments to export");
        return;
      }

      await navigator.clipboard.writeText(text);
      appendMessage("result", "subtitle", `${kind.toUpperCase()} copied`, "export");
    }

    function setRegionCaptureStatus(value) {
      $("regionCaptureStatus").textContent = value;
      regionStage.classList.toggle("active", value === "on");
      $("regionPlaceholder").classList.toggle("hidden", value === "on");
    }

    function setRegionLoopStatus(value) {
      $("regionLoopStatus").textContent = value;
      $("toggleRegionLoopButton").textContent = value === "on" ? "Loop On" : "Loop Off";
      $("settingsToggleRegionLoopButton").textContent = value === "on" ? "Loop On" : "Loop Off";
    }

    function updateRegionSelectionStatus() {
      if (!state.regionSelection) {
        $("regionSelectionStatus").textContent = "-";
        return;
      }

      const region = state.regionSelection;
      $("regionSelectionStatus").textContent =
        `${Math.round(region.x)},${Math.round(region.y)} ${Math.round(region.width)}x${Math.round(region.height)}`;
    }

    function setRegionSelection(region) {
      state.regionSelection = region;
      if (!region || region.width < 4 || region.height < 4) {
        regionSelection.classList.add("hidden");
        updateRegionSelectionStatus();
        return;
      }

      regionSelection.style.left = `${region.x}px`;
      regionSelection.style.top = `${region.y}px`;
      regionSelection.style.width = `${region.width}px`;
      regionSelection.style.height = `${region.height}px`;
      regionSelection.classList.remove("hidden");
      updateRegionSelectionStatus();
    }

    function getRegionPoint(event) {
      const bounds = regionStage.getBoundingClientRect();
      return {
        x: Math.max(0, Math.min(bounds.width, event.clientX - bounds.left)),
        y: Math.max(0, Math.min(bounds.height, event.clientY - bounds.top))
      };
    }

    function updateRegionDrag(point) {
      if (!state.regionDrag) {
        return;
      }

      const start = state.regionDrag;
      const x = Math.min(start.x, point.x);
      const y = Math.min(start.y, point.y);
      const width = Math.abs(point.x - start.x);
      const height = Math.abs(point.y - start.y);
      setRegionSelection({ x, y, width, height });
    }

    function setDefaultRegionSelection() {
      if (state.regionSelection || !state.regionStream || !regionStage.clientWidth || !regionStage.clientHeight) {
        return;
      }

      const width = Math.max(120, regionStage.clientWidth * 0.72);
      const height = Math.max(64, regionStage.clientHeight * 0.28);
      setRegionSelection({
        x: Math.max(0, (regionStage.clientWidth - width) / 2),
        y: Math.max(0, regionStage.clientHeight - height - 24),
        width: Math.min(width, regionStage.clientWidth),
        height: Math.min(height, regionStage.clientHeight)
      });
    }

    async function startRegionCapture() {
      if (!navigator.mediaDevices || !navigator.mediaDevices.getDisplayMedia) {
        throw new Error("screen capture is not supported in this browser");
      }

      stopRegionCapture(false);
      const stream = await navigator.mediaDevices.getDisplayMedia({
        video: { cursor: "always" },
        audio: false
      });

      state.regionStream = stream;
      regionVideo.srcObject = stream;
      await regionVideo.play();
      setRegionCaptureStatus("on");
      appendMessage("result", "region", "screen capture ready", "capture");

      for (const track of stream.getVideoTracks()) {
        track.addEventListener("ended", () => stopRegionCapture());
      }

      window.setTimeout(setDefaultRegionSelection, 120);
    }

    function stopRegionCapture(showMessage = true) {
      stopRegionLoop(false);
      if (state.regionStream) {
        for (const track of state.regionStream.getTracks()) {
          track.stop();
        }
      }

      state.regionStream = null;
      regionVideo.srcObject = null;
      setRegionCaptureStatus("off");
      if (showMessage) {
        appendMessage("result", "region", "screen capture stopped", "capture");
      }
    }

    function getRegionCropBase64() {
      if (!state.regionStream || !regionVideo.videoWidth || !regionVideo.videoHeight) {
        throw new Error("capture a screen first");
      }

      const region = state.regionSelection;
      if (!region || region.width < 4 || region.height < 4) {
        throw new Error("drag a region over the preview first");
      }

      const videoBounds = regionVideo.getBoundingClientRect();
      const stageBounds = regionStage.getBoundingClientRect();
      const offsetX = videoBounds.left - stageBounds.left;
      const offsetY = videoBounds.top - stageBounds.top;
      const scaleX = regionVideo.videoWidth / Math.max(1, videoBounds.width);
      const scaleY = regionVideo.videoHeight / Math.max(1, videoBounds.height);
      const sx = Math.max(0, Math.round((region.x - offsetX) * scaleX));
      const sy = Math.max(0, Math.round((region.y - offsetY) * scaleY));
      const sw = Math.max(1, Math.min(regionVideo.videoWidth - sx, Math.round(region.width * scaleX)));
      const sh = Math.max(1, Math.min(regionVideo.videoHeight - sy, Math.round(region.height * scaleY)));

      const canvas = document.createElement("canvas");
      canvas.width = sw;
      canvas.height = sh;
      const context = canvas.getContext("2d");
      context.drawImage(regionVideo, sx, sy, sw, sh, 0, 0, sw, sh);
      return canvas.toDataURL("image/png").split(",")[1];
    }

    async function runRegionSnapshot(loop = false) {
      if (!state.regionStream) {
        await startRegionCapture();
      }

      setDefaultRegionSelection();
      const cropBase64 = getRegionCropBase64();
      imageBase64.value = cropBase64;
      const previousOcr = regionOcrText.value.trim();
      const previousTranslation = regionTranslationText.value.trim();
      const started = performance.now();
      const result = await post("/ocr/translate", buildOcrTranslatePayload(cropBase64, "image/png"));
      const elapsed = Math.round(performance.now() - started);
      $("latencyLabel").textContent = `${elapsed} ms`;

      const ocrText = result.ocr && result.ocr.text ? result.ocr.text : "";
      const translation = result.translation || {};
      const translatedText = translation.result || "";
      state.latestOcrText = ocrText;
      regionOcrText.value = ocrText;
      regionTranslationText.value = translatedText;
      ocrOutput.value = ocrText;
      sourceText.value = ocrText;
      lastOutput.value = translatedText;
      $("blockCount").textContent = String(result.ocr && result.ocr.blocks ? result.ocr.blocks.length : 0);
      $("engineName").textContent = result.ocr && result.ocr.engine ? result.ocr.engine : "-";

      const changed = ocrText !== previousOcr || translatedText !== previousTranslation;
      if (!loop || changed) {
        appendMessage("user", "region ocr", ocrText, result.ocr && result.ocr.provider ? result.ocr.provider : "");
        if (translation.errorCode && translation.errorCode !== "0") {
          appendMessage("error", "region translation", translation.errorMessage || translatedText, `${elapsed} ms`);
        } else {
          appendMessage("result", "region translation", translatedText, `${elapsed} ms`);
        }
      }

      return result;
    }

    function getRegionInterval() {
      const value = Number.parseInt($("regionInterval").value, 10);
      return Number.isFinite(value) ? Math.max(500, value) : 1500;
    }

    function syncRegionInterval(value) {
      const parsed = Number.parseInt(value, 10);
      const normalized = Number.isFinite(parsed) ? Math.max(500, parsed) : 1500;
      $("regionInterval").value = String(normalized);
      $("regionIntervalSetting").value = String(normalized);
    }

    function stopRegionLoop(showMessage = true) {
      state.regionLoopRunning = false;
      if (state.regionLoopTimer) {
        window.clearTimeout(state.regionLoopTimer);
        state.regionLoopTimer = null;
      }

      setRegionLoopStatus("off");
      if (showMessage) {
        appendMessage("result", "region", "loop stopped", "loop");
      }
    }

    function scheduleRegionLoop() {
      if (!state.regionLoopRunning) {
        return;
      }

      state.regionLoopTimer = window.setTimeout(async () => {
        if (!state.regionLoopRunning) {
          return;
        }

        if (!state.busy) {
          setBusy(true);
          try {
            await runRegionSnapshot(true);
          } catch (error) {
            appendMessage("error", "region loop", error.message || String(error));
            stopRegionLoop(false);
          } finally {
            setBusy(false);
          }
        }

        scheduleRegionLoop();
      }, getRegionInterval());
    }

    async function toggleRegionLoop() {
      if (state.regionLoopRunning) {
        stopRegionLoop();
        return;
      }

      if (!state.regionStream) {
        await startRegionCapture();
      }

      setDefaultRegionSelection();
      getRegionCropBase64();
      state.regionLoopRunning = true;
      setRegionLoopStatus("on");
      appendMessage("result", "region", `loop every ${getRegionInterval()} ms`, "loop");
      scheduleRegionLoop();
    }

    async function runCurrent() {
      if (state.busy) {
        return;
      }

      setBusy(true);
      try {
        if (state.mode === "text") {
          await runTranslate();
        } else if (state.mode === "ocr") {
          await runOcr();
        } else if (state.mode === "audio") {
          await runAsr();
        } else if (state.mode === "audioPipe") {
          await runAsrPipeline();
        } else if (state.mode === "region") {
          await runRegionSnapshot(false);
        } else {
          await runPipeline();
        }
      } catch (error) {
        appendMessage("error", state.mode, error.message || String(error));
      } finally {
        setBusy(false);
      }
    }

    async function readFileAsBase64(file) {
      const buffer = await file.arrayBuffer();
      const bytes = new Uint8Array(buffer);
      let binary = "";
      const chunkSize = 8192;
      for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
      }
      return btoa(binary);
    }

    async function loadImageFile(file, autoRun = true) {
      imageBase64.value = await readFileAsBase64(file);
      $("footerLeft").textContent = file.name;
      setMode("ocr");
      appendMessage("user", "image", `${file.name} (${file.size} bytes)`, "loaded");
      if (autoRun) {
        await runCurrent();
      }
    }

    async function loadAudioFile(file, autoRun = true) {
      audioBase64.value = await readFileAsBase64(file);
      audioSourceUrl.value = "";
      state.audioMimeType = file.type || "application/octet-stream";
      $("footerLeft").textContent = file.name;
      setMode("audio");
      appendMessage("user", "audio", `${file.name} (${file.size} bytes)`, "loaded");
      if (autoRun) {
        await runCurrent();
      }
    }

    function getFirstImageFile(fileList) {
      return Array.from(fileList || []).find(file => file.type && file.type.startsWith("image/")) || null;
    }

    function getFirstAudioFile(fileList) {
      return Array.from(fileList || []).find(file =>
        file.type && (file.type.startsWith("audio/") || file.type.startsWith("video/"))) || null;
    }

    function connectBroadcast() {
      const protocol = location.protocol === "https:" ? "wss:" : "ws:";
      const socket = new WebSocket(`${protocol}//${location.host}/broadcast`);
      socket.addEventListener("open", () => {
        $("wsDot").classList.add("live");
        $("wsStatus").textContent = "live";
      });
      socket.addEventListener("message", event => {
        const message = JSON.parse(event.data);
        if (message.type !== "translation") {
          return;
        }
        $("latestSource").textContent = message.source || "-";
        $("latestTarget").textContent = message.target || "-";
        $("latestProvider").textContent = message.provider || "-";
        playUiSound("notification");
      });
      socket.addEventListener("close", () => {
        $("wsDot").classList.remove("live");
        $("wsStatus").textContent = "reconnect";
        window.setTimeout(connectBroadcast, 1500);
      });
      socket.addEventListener("error", () => socket.close());
    }

    async function boot() {
      try {
        const [health, providers, ocrEngines, speechEngines, presets, glossaries, latest] = await Promise.all([
          api("/health"),
          api("/providers"),
          api("/ocr/engines"),
          api("/asr/engines"),
          api("/presets"),
          api("/glossaries"),
          fetch("/broadcast/latest").then(response => response.status === 204 ? null : response.json())
        ]);

        populateSelect($("translationProvider"), providers, item => item.name, item => item.name, health.defaultProvider);
        populateOcrEngines(ocrEngines, health.ocr && health.ocr.defaultProvider);
        populateSpeechEngines(speechEngines, health.speech && health.speech.defaultProvider);
        populateSelect($("preset"), presets, item => item.id, item => item.id, health.defaultMode);
        populateSelect($("glossary"), [{ id: "", name: "none" }, ...glossaries], item => item.id || "", item => item.name || item.id || "none", "");

        $("metricProvider").textContent = $("translationProvider").value || "-";
        $("translationModel").value = health.ollama && health.ollama.model ? health.ollama.model : "";
        await loadTranslationModels();
        $("metricOcr").textContent = $("ocrProvider").value || "-";
        $("metricAsr").textContent = $("speechProvider").value || "-";
        $("healthStatus").textContent = "ok";
        $("ocrLanguage").value = health.ocr && health.ocr.defaultLanguage ? health.ocr.defaultLanguage : "ja";
        $("speechLanguage").value = health.speech && health.speech.defaultLanguage ? health.speech.defaultLanguage : "ja";

        if (latest) {
          $("latestSource").textContent = latest.source || "-";
          $("latestTarget").textContent = latest.target || "-";
          $("latestProvider").textContent = latest.provider || "-";
        }

        appendMessage("result", "system", "YomiBridge app ready", "boot");
      } catch (error) {
        $("healthStatus").textContent = "error";
        appendMessage("error", "boot", error.message || String(error));
      }

      connectBroadcast();
    }

    $("runButton").addEventListener("click", runCurrent);
    $("translateOcrTextButton").addEventListener("click", translateEditedOcrText);
    $("clearButton").addEventListener("click", () => {
      terminal.innerHTML = "";
      lastOutput.value = "";
      ocrOutput.value = "";
      asrOutput.value = "";
      speechSegmentsTable.innerHTML = "";
      state.latestSpeechSegments = [];
      state.latestSpeechTranslations = [];
      state.history = [];
      renderSessions();
    });
    $("tabTranslate").addEventListener("click", () => setMode("text"));
    $("tabOcr").addEventListener("click", () => setMode("ocr"));
    $("tabPipeline").addEventListener("click", () => setMode("pipe"));
    $("tabAudio").addEventListener("click", () => setMode("audio"));
    $("tabAudioPipeline").addEventListener("click", () => setMode("audioPipe"));
    $("tabRegion").addEventListener("click", () => setMode("region"));
    $("tabSettings").addEventListener("click", () => setMode("settings"));
    $("modeTranslate").addEventListener("click", () => setMode("text"));
    $("modeOcr").addEventListener("click", () => setMode("ocr"));
    $("modePipeline").addEventListener("click", () => setMode("pipe"));
    $("modeAudio").addEventListener("click", () => setMode("audio"));
    $("modeAudioPipeline").addEventListener("click", () => setMode("audioPipe"));
    $("modeRegion").addEventListener("click", () => setMode("region"));
    $("modeSettings").addEventListener("click", () => setMode("settings"));
    for (const button of document.querySelectorAll("[data-settings-section]")) {
      button.addEventListener("click", () => setSettingsSection(button.dataset.settingsSection));
    }
    $("soundEnabled").addEventListener("change", readSoundControls);
    $("soundClick").addEventListener("change", readSoundControls);
    $("soundSuccess").addEventListener("change", readSoundControls);
    $("soundError").addEventListener("change", readSoundControls);
    $("soundNotification").addEventListener("change", readSoundControls);
    $("soundTestClickButton").addEventListener("click", () => playUiSound("click", true));
    $("soundTestSuccessButton").addEventListener("click", () => playUiSound("success", true));
    $("soundTestErrorButton").addEventListener("click", () => playUiSound("error", true));
    $("translationProvider").addEventListener("change", () => {
      $("metricProvider").textContent = $("translationProvider").value || "-";
      loadTranslationModels();
    });
    $("translationModel").addEventListener("input", () => $("metricModel").textContent = $("translationModel").value || "-");
    $("ocrProvider").addEventListener("change", () => {
      $("metricOcr").textContent = $("ocrProvider").value || "-";
      syncOcrHint();
      pulseOcrProviderHelp();
    });
    $("ocrProvider").addEventListener("focus", pulseOcrProviderHelp);
    $("ocrProvider").addEventListener("mouseenter", pulseOcrProviderHelp);
    $("speechProvider").addEventListener("change", () => {
      $("metricAsr").textContent = $("speechProvider").value || "-";
      syncSpeechHint();
    });
    $("copySrtButton").addEventListener("click", () => {
      copySubtitle("srt").catch(error => appendMessage("error", "subtitle", error.message || String(error)));
    });
    $("copyVttButton").addEventListener("click", () => {
      copySubtitle("vtt").catch(error => appendMessage("error", "subtitle", error.message || String(error)));
    });
    $("startRegionCaptureButton").addEventListener("click", () => {
      startRegionCapture().catch(error => appendMessage("error", "region", error.message || String(error)));
    });
    $("snapshotRegionButton").addEventListener("click", async () => {
      if (state.busy) {
        return;
      }

      setMode("region");
      setBusy(true);
      try {
        await runRegionSnapshot(false);
      } catch (error) {
        appendMessage("error", "region", error.message || String(error));
      } finally {
        setBusy(false);
      }
    });
    $("toggleRegionLoopButton").addEventListener("click", () => {
      toggleRegionLoop().catch(error => appendMessage("error", "region", error.message || String(error)));
    });
    $("stopRegionCaptureButton").addEventListener("click", () => stopRegionCapture());
    $("settingsCaptureButton").addEventListener("click", () => {
      setMode("region");
      startRegionCapture().catch(error => appendMessage("error", "region", error.message || String(error)));
    });
    $("settingsSnapshotButton").addEventListener("click", async () => {
      setMode("region");
      if (state.busy) {
        return;
      }

      setBusy(true);
      try {
        await runRegionSnapshot(false);
      } catch (error) {
        appendMessage("error", "region", error.message || String(error));
      } finally {
        setBusy(false);
      }
    });
    $("settingsToggleRegionLoopButton").addEventListener("click", () => {
      setMode("region");
      toggleRegionLoop().catch(error => appendMessage("error", "region", error.message || String(error)));
    });
    $("regionInterval").addEventListener("input", () => syncRegionInterval($("regionInterval").value));
    $("regionIntervalSetting").addEventListener("input", () => syncRegionInterval($("regionIntervalSetting").value));
    regionVideo.addEventListener("loadedmetadata", () => window.setTimeout(setDefaultRegionSelection, 120));
    regionStage.addEventListener("pointerdown", event => {
      if (!state.regionStream) {
        return;
      }

      event.preventDefault();
      regionStage.setPointerCapture(event.pointerId);
      const point = getRegionPoint(event);
      state.regionDrag = point;
      setRegionSelection({ x: point.x, y: point.y, width: 0, height: 0 });
    });
    regionStage.addEventListener("pointermove", event => {
      if (!state.regionDrag) {
        return;
      }

      updateRegionDrag(getRegionPoint(event));
    });
    regionStage.addEventListener("pointerup", event => {
      if (!state.regionDrag) {
        return;
      }

      updateRegionDrag(getRegionPoint(event));
      state.regionDrag = null;
      try {
        regionStage.releasePointerCapture(event.pointerId);
      } catch {
      }
    });
    imageFile.addEventListener("change", event => {
      const file = event.target.files && event.target.files[0];
      if (file) {
        loadImageFile(file, true).catch(error => appendMessage("error", "file", error.message || String(error)));
      }
    });
    audioFile.addEventListener("change", event => {
      const file = event.target.files && event.target.files[0];
      if (file) {
        loadAudioFile(file, false).catch(error => appendMessage("error", "audio", error.message || String(error)));
      }
    });
    dropZone.addEventListener("dragover", event => {
      event.preventDefault();
      dropZone.classList.add("dragging");
    });
    dropZone.addEventListener("dragleave", () => dropZone.classList.remove("dragging"));
    dropZone.addEventListener("drop", event => {
      event.preventDefault();
      dropZone.classList.remove("dragging");
      const file = getFirstImageFile(event.dataTransfer.files);
      if (!file) {
        appendMessage("error", "image", "drop an image file");
        return;
      }
      loadImageFile(file, true).catch(error => appendMessage("error", "file", error.message || String(error)));
    });
    document.addEventListener("paste", event => {
      const items = Array.from(event.clipboardData ? event.clipboardData.items : []);
      const imageItem = items.find(item => item.kind === "file" && item.type.startsWith("image/"));
      if (!imageItem) {
        return;
      }

      const file = imageItem.getAsFile();
      if (!file) {
        return;
      }

      event.preventDefault();
      loadImageFile(file, true).catch(error => appendMessage("error", "paste", error.message || String(error)));
    });
    document.addEventListener("click", event => {
      const button = event.target.closest("button");
      if (!button || button.id.startsWith("soundTest")) {
        return;
      }

      playUiSound("click");
    }, true);
    window.addEventListener("beforeunload", () => stopRegionCapture(false));

    syncSoundControls();
    boot();
  </script>
</body>
</html>
""";
}
