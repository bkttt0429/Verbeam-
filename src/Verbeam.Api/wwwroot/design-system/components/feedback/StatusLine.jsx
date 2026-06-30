import React from "react";

/**
 * StatusLine — a label/value row used throughout panels and settings
 * status stacks (muted label left, strong value right).
 */
export function StatusLine({ label, value, valueColor, style, ...rest }) {
  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        gap: "10px",
        color: "var(--text-muted)",
        fontFamily: "var(--font-mono)",
        fontSize: "var(--text-sm)",
        ...style,
      }}
      {...rest}
    >
      <span>{label}</span>
      <strong style={{ color: valueColor || "var(--text-body)", fontWeight: "var(--weight-strong)" }}>
        {value}
      </strong>
    </div>
  );
}
