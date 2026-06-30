import React from "react";

/**
 * NavButton — a full-width sidebar navigation item. Ghost by default,
 * filled grey when active. Used for the workspace nav and settings nav.
 */
export function NavButton({ children, active = false, style, ...rest }) {
  const [hover, setHover] = React.useState(false);
  const bg = active ? "var(--vb-active-bg)" : hover ? "rgba(255,255,255,0.05)" : "transparent";
  const color = active ? "var(--accent)" : hover ? "var(--text-strong)" : "#969696";

  return (
    <button
      type="button"
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: "inline-flex",
        alignItems: "center",
        justifyContent: "flex-start",
        gap: "8px",
        width: "100%",
        minHeight: "34px",
        padding: "0 12px",
        border: "1px solid transparent",
        borderRadius: "var(--radius-md)",
        background: bg,
        color,
        boxShadow: active ? "var(--ring-active)" : "none",
        fontFamily: "var(--font-sans)",
        fontSize: "var(--text-base)",
        fontWeight: active ? "var(--weight-strong)" : "var(--weight-normal)",
        cursor: "pointer",
        transition: "var(--transition-control)",
        ...style,
      }}
      {...rest}
    >
      {children}
    </button>
  );
}
