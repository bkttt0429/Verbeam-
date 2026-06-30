import React from "react";

/**
 * Field — a mono label stacked above a control. The workbench's
 * universal form-row wrapper.
 */
export function Field({ label, htmlFor, hint, children, style, ...rest }) {
  return (
    <div style={{ display: "grid", gap: "6px", minWidth: 0, ...style }} {...rest}>
      {label ? (
        <label
          htmlFor={htmlFor}
          style={{
            fontFamily: "var(--font-mono)",
            fontSize: "var(--text-xs)",
            fontWeight: "var(--weight-bold)",
            color: "var(--text-label)",
          }}
        >
          {label}
        </label>
      ) : null}
      {children}
      {hint ? (
        <div style={{ color: "var(--text-muted)", fontSize: "var(--text-sm)", lineHeight: "var(--leading-normal)" }}>
          {hint}
        </div>
      ) : null}
    </div>
  );
}
