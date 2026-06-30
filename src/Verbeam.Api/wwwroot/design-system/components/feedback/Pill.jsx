import React from "react";

/**
 * Pill — compact status capsule used in the topbar, footer and composer
 * (e.g. broadcast state, latency, mode). Optional leading status dot.
 */
export function Pill({ children, dot, style, ...rest }) {
  const dotColors = {
    live: "var(--success)",
    idle: "var(--warning)",
    error: "var(--danger)",
    neutral: "var(--text-muted)",
  };

  return (
    <span
      style={{
        display: "inline-flex",
        alignItems: "center",
        gap: "7px",
        minHeight: "22px",
        padding: "0 8px",
        border: "1px solid var(--border-hairline)",
        borderRadius: "var(--radius-sm)",
        background: "var(--surface-panel)",
        color: "var(--text-muted)",
        fontFamily: "var(--font-mono)",
        fontSize: "var(--text-xs)",
        whiteSpace: "nowrap",
        ...style,
      }}
      {...rest}
    >
      {dot ? (
        <span
          style={{
            width: "7px",
            height: "7px",
            borderRadius: "50%",
            background: dotColors[dot] || dotColors.neutral,
          }}
        />
      ) : null}
      {children}
    </span>
  );
}
