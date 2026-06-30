import React from "react";

/**
 * Tab — a compact lowercase mode tab used in the composer (text / ocr /
 * pipe / audio / region / settings). Ghost until active.
 */
export function Tab({ children, active = false, style, ...rest }) {
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
        justifyContent: "center",
        minHeight: "34px",
        padding: "0 12px",
        border: "1px solid transparent",
        borderRadius: "var(--radius-md)",
        background: bg,
        color,
        boxShadow: active ? "var(--ring-active)" : "none",
        fontFamily: "var(--font-sans)",
        fontSize: "var(--text-sm)",
        fontWeight: active ? "var(--weight-strong)" : "var(--weight-normal)",
        cursor: "pointer",
        whiteSpace: "nowrap",
        transition: "var(--transition-control)",
        ...style,
      }}
      {...rest}
    >
      {children}
    </button>
  );
}
