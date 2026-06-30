import React from "react";

/**
 * Metric — a runtime read-out row in the inspector: a RemixIcon glyph,
 * a flexible mono label, and a strong right-aligned value.
 */
export function Metric({ icon = "ri-server-line", label, value = "-", style, ...rest }) {
  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        gap: "8px",
        minHeight: "32px",
        minWidth: 0,
        border: "1px solid var(--border-hairline)",
        borderRadius: "var(--radius-md)",
        background: "rgba(255,255,255,0.02)",
        color: "var(--text-meta)",
        fontFamily: "var(--font-mono)",
        fontSize: "var(--text-sm)",
        fontWeight: "var(--weight-medium)",
        padding: "0 10px",
        ...style,
      }}
      {...rest}
    >
      <i className={icon} style={{ fontSize: "14px", color: "var(--text-muted)", flexShrink: 0 }} />
      <span style={{ flex: 1, marginLeft: "4px" }}>{label}</span>
      <strong style={{ color: "#f1f5f9", fontWeight: "var(--weight-strong)" }}>{value}</strong>
    </div>
  );
}
