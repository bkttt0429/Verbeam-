/* global React */
const { useState } = React;
const { Pill } = window.LocalTranslateHubDesignSystem_32566a;

/* The fixed app chrome: topbar, activity rail, footer. Pure presentation. */

function Topbar({ health = "ok", broadcast = "broadcast", live = false }) {
  return (
    <header style={tbStyles.bar}>
      <div style={tbStyles.brand}><span style={tbStyles.mark}>OC</span><span>Verbeam</span></div>
      <div style={tbStyles.actions}>
        <Pill dot={live ? "live" : "idle"}>{broadcast}</Pill>
        <Pill>{health}</Pill>
      </div>
    </header>
  );
}

const tbStyles = {
  bar: {
    gridColumn: "1 / -1",
    display: "grid",
    gridTemplateColumns: "1fr auto 1fr",
    alignItems: "center",
    height: "44px",
    padding: "0 12px",
    borderBottom: "1px solid var(--vb-line)",
    background: "#0d0d0d",
  },
  brand: {
    gridColumn: 2,
    display: "inline-flex",
    alignItems: "center",
    gap: "8px",
    color: "#f1f1f1",
    fontFamily: "var(--font-mono)",
    fontSize: "12px",
    fontWeight: 650,
  },
  mark: { color: "var(--vb-muted)", fontSize: "11px", fontWeight: 650 },
  actions: { gridColumn: 3, justifySelf: "end", display: "flex", alignItems: "center", gap: "6px" },
};

function ActivityRail({ route = "/app" }) {
  const links = [
    { icon: "ri-terminal-box-line", href: "/app", url: "../workbench/index.html", title: "Workbench" },
    { icon: "ri-broadcast-line", href: "/viewer", url: "../viewer/index.html", title: "Viewer" },
    { icon: "ri-projector-line", href: "/projector", url: "../projector/index.html", title: "Projector" },
  ];
  return (
    <aside style={railStyles.rail}>
      <div style={railStyles.group}>
        <div style={railStyles.badge}>LTH</div>
        {links.map((l) => (
          <RailLink key={l.href} {...l} active={l.href === route} />
        ))}
      </div>
      <div style={railStyles.group}>
        <RailLink icon="ri-pulse-line" href="/health" title="Health" />
      </div>
    </aside>
  );
}

function RailLink({ icon, title, active, url }) {
  const [hover, setHover] = useState(false);
  return (
    <a
      href={url || "#"}
      title={title}
      onClick={(e) => { if (!url || active) e.preventDefault(); }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        ...railStyles.link,
        borderColor: active || hover ? "var(--vb-line-strong)" : "transparent",
        background: active || hover ? "var(--vb-rail-active)" : "transparent",
        color: active || hover ? "var(--vb-text)" : "var(--vb-muted)",
      }}
    >
      <i className={icon} />
    </a>
  );
}

const railStyles = {
  rail: {
    gridColumn: 1,
    gridRow: "2 / 3",
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "space-between",
    padding: "12px 8px",
    borderRight: "1px solid var(--vb-line)",
    background: "var(--vb-rail)",
  },
  group: { display: "grid", gap: "12px", justifyItems: "center", width: "100%" },
  badge: {
    display: "inline-grid",
    placeItems: "center",
    minHeight: "28px",
    padding: "0 8px",
    border: "1px solid var(--vb-line)",
    borderRadius: "6px",
    background: "#161616",
    color: "var(--vb-text)",
    fontFamily: "var(--font-mono)",
    fontSize: "10px",
    fontWeight: 650,
  },
  link: {
    display: "inline-grid",
    placeItems: "center",
    width: "36px",
    height: "36px",
    border: "1px solid transparent",
    borderRadius: "6px",
    fontSize: "18px",
    textDecoration: "none",
    transition: "all 0.2s ease",
  },
};

function Footer({ left = "ready", right = "/app" }) {
  return (
    <footer style={ftStyles.footer}>
      <span>{left}</span>
      <span>{right}</span>
    </footer>
  );
}

const ftStyles = {
  footer: {
    gridColumn: "1 / -1",
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    height: "30px",
    padding: "0 14px",
    borderTop: "1px solid var(--vb-line)",
    background: "#0d0d0d",
    color: "var(--vb-muted)",
    fontFamily: "var(--font-mono)",
    fontSize: "11px",
  },
};

Object.assign(window, { VbTopbar: Topbar, VbActivityRail: ActivityRail, VbFooter: Footer });
