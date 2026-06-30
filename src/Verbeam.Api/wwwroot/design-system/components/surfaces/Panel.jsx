import React from "react";

/**
 * Panel — a titled surface card used in the inspector and elsewhere.
 * A title bar (with optional trailing action) over a padded body.
 */
export function Panel({ title, action, children, bodyStyle, style, ...rest }) {
  return (
    <section
      style={{
        border: "1px solid var(--border-hairline)",
        borderRadius: "var(--radius-lg)",
        background: "#171717",
        overflow: "hidden",
        ...style,
      }}
      {...rest}
    >
      {title ? (
        <div
          style={{
            display: "flex",
            alignItems: "center",
            justifyContent: "space-between",
            minHeight: "34px",
            padding: "0 10px",
            borderBottom: "1px solid var(--border-hairline)",
            background: "#191919",
            color: "#b8b8b8",
            fontFamily: "var(--font-mono)",
            fontSize: "var(--text-sm)",
            fontWeight: "var(--weight-bold)",
          }}
        >
          <span>{title}</span>
          {action}
        </div>
      ) : null}
      <div style={{ display: "grid", gap: "12px", padding: "12px", ...bodyStyle }}>{children}</div>
    </section>
  );
}
