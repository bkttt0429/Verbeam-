import React from "react";

/**
 * Verbeam Button.
 * Flat workbench button: hairline border, raised panel-2 fill, subtle
 * white hover wash. Variants: primary (solid blue), secondary (default),
 * ghost (transparent), danger (solid red).
 */
export function Button({
  children,
  variant = "secondary",
  size = "md",
  commandKey,
  icon,
  fullWidth = false,
  disabled = false,
  type = "button",
  style,
  ...rest
}) {
  const heights = { sm: "28px", md: "34px" };

  const base = {
    display: "inline-flex",
    alignItems: "center",
    justifyContent: "center",
    gap: "8px",
    minHeight: heights[size] || heights.md,
    padding: size === "sm" ? "0 12px" : "0 16px",
    border: "1px solid var(--border-control)",
    borderRadius: "var(--radius-md)",
    background: "var(--surface-raised)",
    color: "#eeeeee",
    fontFamily: "var(--font-sans)",
    fontSize: "var(--text-base)",
    fontWeight: "var(--weight-medium)",
    lineHeight: 1,
    width: fullWidth ? "100%" : undefined,
    cursor: disabled ? "not-allowed" : "pointer",
    opacity: disabled ? 0.55 : 1,
    whiteSpace: "nowrap",
    transition: "var(--transition-control)",
  };

  const variants = {
    primary: {
      border: "1px solid transparent",
      background: "var(--accent)",
      color: "#ffffff",
    },
    secondary: {},
    ghost: {
      border: "1px solid transparent",
      background: "transparent",
      color: "var(--text-muted)",
    },
    danger: {
      border: "1px solid transparent",
      background: "var(--danger)",
      color: "#ffffff",
    },
  };

  const [hover, setHover] = React.useState(false);
  const hoverStyles = !disabled && hover ? hoverFor(variant) : null;

  return (
    <button
      type={type}
      disabled={disabled}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{ ...base, ...variants[variant], ...hoverStyles, ...style }}
      {...rest}
    >
      {commandKey ? (
        <span style={{ color: "var(--success)", fontFamily: "var(--font-mono)", fontWeight: 700 }}>{commandKey}</span>
      ) : null}
      {icon}
      {children ? <span>{children}</span> : null}
    </button>
  );
}

function hoverFor(variant) {
  switch (variant) {
    case "primary":
      return { background: "var(--accent-hover)" };
    case "ghost":
      return { background: "var(--surface-hover)", color: "var(--text-body)" };
    case "danger":
      return { background: "var(--danger-hover)" };
    default:
      return { background: "var(--surface-hover)", borderColor: "var(--border-hover)" };
  }
}
