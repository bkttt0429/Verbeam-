import React from "react";

/**
 * LocalTranslateHub select dropdown. Matches the dark field styling.
 */
export function Select({ options = [], value, onChange, invalid = false, style, children, ...rest }) {
  const [focused, setFocused] = React.useState(false);

  return (
    <div style={{ position: "relative", width: "100%" }}>
      <select
        value={value}
        onChange={onChange}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        style={{
          width: "100%",
          minHeight: "34px",
          padding: "0 30px 0 12px",
          border: `1px solid ${invalid ? "var(--danger)" : "var(--border-control)"}`,
          borderColor: focused ? "var(--accent)" : undefined,
          borderRadius: "var(--radius-sm)",
          background: "var(--surface-field)",
          color: "#eeeeee",
          fontFamily: "var(--font-sans)",
          fontSize: "var(--text-base)",
          outline: "none",
          appearance: "none",
          WebkitAppearance: "none",
          MozAppearance: "none",
          cursor: "pointer",
          transition: "var(--transition-control)",
          ...style,
        }}
        {...rest}
      >
        {children ||
          options.map((opt) => {
            const o = typeof opt === "string" ? { value: opt, label: opt } : opt;
            return (
              <option key={o.value} value={o.value} disabled={o.disabled}>
                {o.label ?? o.value}
              </option>
            );
          })}
      </select>
      <span
        aria-hidden="true"
        style={{
          position: "absolute",
          right: "10px",
          top: "50%",
          transform: "translateY(-50%)",
          color: "var(--text-muted)",
          fontSize: "10px",
          pointerEvents: "none",
        }}
      >
        ▾
      </span>
    </div>
  );
}
