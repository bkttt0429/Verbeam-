import React from "react";

/**
 * LocalTranslateHub text input. Dark field, blue focus ring.
 * Supports single-line (default) and multiline (textarea, mono).
 */
export function Input({
  multiline = false,
  mono = false,
  rows = 5,
  invalid = false,
  style,
  ...rest
}) {
  const [focused, setFocused] = React.useState(false);

  const shared = {
    width: "100%",
    border: `1px solid ${invalid ? "var(--danger)" : "var(--border-control)"}`,
    borderRadius: "var(--radius-sm)",
    background: "var(--surface-field)",
    color: "#eeeeee",
    fontFamily: mono || multiline ? "var(--font-mono)" : "var(--font-sans)",
    outline: "none",
    borderColor: focused ? "var(--accent)" : undefined,
    transition: "var(--transition-control)",
  };

  if (multiline) {
    return (
      <textarea
        rows={rows}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        style={{
          ...shared,
          minHeight: "126px",
          padding: "10px",
          fontSize: "12.5px",
          lineHeight: "var(--leading-relaxed)",
          resize: "vertical",
          ...style,
        }}
        {...rest}
      />
    );
  }

  return (
    <input
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={{
        ...shared,
        minHeight: "34px",
        padding: "0 12px",
        fontSize: "var(--text-base)",
        ...style,
      }}
      {...rest}
    />
  );
}
