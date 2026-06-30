import React from "react";

/**
 * Notice — an inline amber advisory box used for engine hints and
 * provider guidance.
 */
export function Notice({ children, style, ...rest }) {
  return (
    <div
      style={{
        border: "1px solid var(--lth-notice-border)",
        borderRadius: "var(--radius-sm)",
        background: "var(--lth-notice-bg)",
        color: "var(--lth-notice-fg)",
        padding: "9px",
        fontSize: "var(--text-sm)",
        lineHeight: "var(--leading-normal)",
        ...style,
      }}
      {...rest}
    >
      {children}
    </div>
  );
}
