/* global React */
const { useState: useSbState } = React;
const { NavButton, Metric } = window.LocalTranslateHubDesignSystem_32566a;

const WORKSPACES = [
  { id: "translate", label: "Translate", icon: "ri-translate-2" },
  { id: "ocr", label: "OCR", icon: "ri-scan-2-line" },
  { id: "pipeline", label: "OCR + Translate", icon: "ri-bubble-chart-line" },
  { id: "audio", label: "Audio", icon: "ri-mic-line" },
  { id: "audioPipeline", label: "Audio + Translate", icon: "ri-voiceprint-line" },
  { id: "region", label: "Region", icon: "ri-focus-3-line" },
  { id: "settings", label: "Settings", icon: "ri-settings-3-line" },
];

function Sidebar({ active = "translate", onSelect, runtime }) {
  const [runtimeOpen, setRuntimeOpen] = useSbState(false);
  return (
    <aside style={sbStyles.sidebar}>
      <div style={sbStyles.section}>
        <div className="vb-label" style={sbStyles.title}>workspace</div>
        <div style={sbStyles.nav}>
          {WORKSPACES.map((w) => (
            <NavButton key={w.id} active={active === w.id} onClick={() => onSelect && onSelect(w.id)}>
              <i className={w.icon} style={{ fontSize: "15px", width: "18px", textAlign: "center" }} />
              {w.label}
            </NavButton>
          ))}
        </div>
      </div>
      <div style={sbStyles.section}>
        <button
          type="button"
          onClick={() => setRuntimeOpen(!runtimeOpen)}
          aria-expanded={runtimeOpen}
          title={runtimeOpen ? "Hide runtime details" : "Show runtime details"}
          style={sbStyles.runtimeToggle}
        >
          <span className="vb-label">runtime</span>
          <span style={sbStyles.runtimeSummary}>{runtime.model}</span>
          <i className={runtimeOpen ? "ri-arrow-up-s-line" : "ri-arrow-down-s-line"} style={sbStyles.runtimeChevron} />
        </button>
        {runtimeOpen && (
          <div style={sbStyles.metrics}>
            <Metric icon="ri-database-2-line" label="provider" value={runtime.provider} />
            <Metric icon="ri-cpu-line" label="model" value={runtime.model} />
            <Metric icon="ri-file-text-line" label="ocr" value={runtime.ocr} />
            <Metric icon="ri-sound-module-line" label="asr" value={runtime.asr} />
            <Metric icon="ri-server-line" label="cache" value={runtime.cache} />
          </div>
        )}
      </div>
    </aside>
  );
}

const sbStyles = {
  sidebar: {
    gridColumn: 2,
    gridRow: "2 / 3",
    borderRight: "1px solid var(--vb-line)",
    background: "var(--vb-panel)",
    display: "grid",
    gridTemplateRows: "auto auto",
    alignContent: "start",
    minWidth: 0,
    overflow: "auto",
  },
  section: { padding: "18px 14px", borderBottom: "1px solid var(--vb-line)" },
  title: { marginBottom: "12px" },
  nav: { display: "grid", gap: "4px" },
  metrics: { display: "grid", gap: "8px", marginTop: "12px" },
  runtimeToggle: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    width: "100%",
    minHeight: "28px",
    padding: "0",
    border: "none",
    background: "transparent",
    cursor: "pointer",
    textAlign: "left",
  },
  runtimeSummary: {
    marginLeft: "auto",
    color: "var(--vb-muted)",
    fontFamily: "var(--font-mono)",
    fontSize: "11px",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  runtimeChevron: { color: "var(--vb-muted)", fontSize: "14px", flex: "none" },
};

Object.assign(window, { VbSidebar: Sidebar });
