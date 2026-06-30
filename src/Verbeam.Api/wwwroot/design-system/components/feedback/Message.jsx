import React from "react";

/**
 * Message — a terminal log entry in the workbench. A bordered card with a
 * mono header (title + meta) and a wrapped body. The header tints by kind:
 * user (blue), result (green), error (red), system (muted).
 */
export function Message({ kind = "result", title, meta, children, style, ...rest }) {
  const headColors = {
    user: "#60a5fa",
    result: "var(--success)",
    error: "var(--danger)",
    system: "var(--text-muted)",
  };

  return (
    <article
      style={{
        border: "1px solid var(--border-hairline)",
        borderRadius: "var(--radius-lg)",
        background: "#1a1a1a",
        overflow: "hidden",
        ...style,
      }}
      {...rest}
    >
      <div
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          gap: "10px",
          minHeight: "32px",
          padding: "0 10px",
          borderBottom: "1px solid var(--border-hairline)",
          background: "#181818",
          color: headColors[kind] || headColors.result,
          fontFamily: "var(--font-mono)",
          fontSize: "var(--text-xs)",
        }}
      >
        <span>{title}</span>
        {meta ? <span style={{ color: "var(--text-faint)" }}>{meta}</span> : null}
      </div>
      <div
        style={{
          padding: "14px",
          color: "#d7d7d7",
          fontSize: "var(--text-base)",
          lineHeight: "var(--leading-relaxed)",
          whiteSpace: "pre-wrap",
          wordBreak: "break-word",
        }}
      >
        {children}
      </div>
    </article>
  );
}
