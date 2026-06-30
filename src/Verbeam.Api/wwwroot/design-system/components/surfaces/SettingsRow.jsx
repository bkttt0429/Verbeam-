import React from "react";

/**
 * SettingsRow — the settings-v2 row: a left copy block (title + muted
 * description) and a right-aligned control slot, divided by a hairline.
 */
export function SettingsRow({ title, description, control, last = false, style, ...rest }) {
  return (
    <div
      style={{
        display: "flex",
        alignItems: "flex-start",
        justifyContent: "space-between",
        gap: "24px",
        padding: "16px 0",
        borderBottom: last ? "none" : "1px solid var(--border-hairline)",
        ...style,
      }}
      {...rest}
    >
      <div style={{ display: "grid", gap: "4px", maxWidth: "52%" }}>
        <div style={{ color: "var(--text-body)", fontSize: "var(--text-md)", fontWeight: "var(--weight-strong)" }}>
          {title}
        </div>
        {description ? (
          <div style={{ color: "var(--text-muted)", fontSize: "var(--text-sm)", lineHeight: "var(--leading-normal)" }}>
            {description}
          </div>
        ) : null}
      </div>
      <div style={{ flex: "0 0 auto", minWidth: "200px", maxWidth: "44%", display: "grid", gap: "8px" }}>
        {control}
      </div>
    </div>
  );
}
