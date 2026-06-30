/* @ds-bundle: {"format":3,"namespace":"LocalTranslateHubDesignSystem_32566a","components":[{"name":"Message","sourcePath":"components/feedback/Message.jsx"},{"name":"Notice","sourcePath":"components/feedback/Notice.jsx"},{"name":"Pill","sourcePath":"components/feedback/Pill.jsx"},{"name":"StatusLine","sourcePath":"components/feedback/StatusLine.jsx"},{"name":"Button","sourcePath":"components/forms/Button.jsx"},{"name":"Field","sourcePath":"components/forms/Field.jsx"},{"name":"Input","sourcePath":"components/forms/Input.jsx"},{"name":"Select","sourcePath":"components/forms/Select.jsx"},{"name":"NavButton","sourcePath":"components/navigation/NavButton.jsx"},{"name":"Tab","sourcePath":"components/navigation/Tab.jsx"},{"name":"Metric","sourcePath":"components/surfaces/Metric.jsx"},{"name":"Panel","sourcePath":"components/surfaces/Panel.jsx"},{"name":"SettingsRow","sourcePath":"components/surfaces/SettingsRow.jsx"}],"sourceHashes":{"components/feedback/Message.jsx":"1f7ae3a1aea1","components/feedback/Notice.jsx":"3816416c94f3","components/feedback/Pill.jsx":"ef506e4c2dc4","components/feedback/StatusLine.jsx":"24ceaa3320ae","components/forms/Button.jsx":"a76aaabcb04c","components/forms/Field.jsx":"198e2a9f2b51","components/forms/Input.jsx":"cf6fbf456b7c","components/forms/Select.jsx":"a73aadd5af0b","components/navigation/NavButton.jsx":"0a02d21b7a45","components/navigation/Tab.jsx":"b569aa3b2f58","components/surfaces/Metric.jsx":"448e504d6642","components/surfaces/Panel.jsx":"68946bb9def3","components/surfaces/SettingsRow.jsx":"a7b838c607fc","ui_kits/workbench/Chrome.jsx":"33709077c38a","ui_kits/workbench/Composer.jsx":"9156644e55b3","ui_kits/workbench/SettingsScreen.jsx":"fe8c206c930c","ui_kits/workbench/Sidebar.jsx":"e31bb20b00ae","ui_kits/workbench/WorkbenchApp.jsx":"a1fe6fc8eb27"},"inlinedExternals":[],"unexposedExports":[]} */

(() => {

const __ds_ns = (window.LocalTranslateHubDesignSystem_32566a = window.LocalTranslateHubDesignSystem_32566a || {});

const __ds_scope = {};

(__ds_ns.__errors = __ds_ns.__errors || []);

// components/feedback/Message.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * Message — a terminal log entry in the workbench. A bordered card with a
 * mono header (title + meta) and a wrapped body. The header tints by kind:
 * user (blue), result (green), error (red), system (muted).
 */
function Message({
  kind = "result",
  title,
  meta,
  children,
  style,
  ...rest
}) {
  const headColors = {
    user: "#60a5fa",
    result: "var(--success)",
    error: "var(--danger)",
    system: "var(--text-muted)"
  };
  return /*#__PURE__*/React.createElement("article", _extends({
    style: {
      border: "1px solid var(--border-hairline)",
      borderRadius: "var(--radius-lg)",
      background: "#1a1a1a",
      overflow: "hidden",
      ...style
    }
  }, rest), /*#__PURE__*/React.createElement("div", {
    style: {
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
      fontSize: "var(--text-xs)"
    }
  }, /*#__PURE__*/React.createElement("span", null, title), meta ? /*#__PURE__*/React.createElement("span", {
    style: {
      color: "var(--text-faint)"
    }
  }, meta) : null), /*#__PURE__*/React.createElement("div", {
    style: {
      padding: "14px",
      color: "#d7d7d7",
      fontSize: "var(--text-base)",
      lineHeight: "var(--leading-relaxed)",
      whiteSpace: "pre-wrap",
      wordBreak: "break-word"
    }
  }, children));
}
Object.assign(__ds_scope, { Message });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/feedback/Message.jsx", error: String((e && e.message) || e) }); }

// components/feedback/Notice.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * Notice — an inline amber advisory box used for engine hints and
 * provider guidance.
 */
function Notice({
  children,
  style,
  ...rest
}) {
  return /*#__PURE__*/React.createElement("div", _extends({
    style: {
      border: "1px solid var(--lth-notice-border)",
      borderRadius: "var(--radius-sm)",
      background: "var(--lth-notice-bg)",
      color: "var(--lth-notice-fg)",
      padding: "9px",
      fontSize: "var(--text-sm)",
      lineHeight: "var(--leading-normal)",
      ...style
    }
  }, rest), children);
}
Object.assign(__ds_scope, { Notice });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/feedback/Notice.jsx", error: String((e && e.message) || e) }); }

// components/feedback/Pill.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * Pill — compact status capsule used in the topbar, footer and composer
 * (e.g. broadcast state, latency, mode). Optional leading status dot.
 */
function Pill({
  children,
  dot,
  style,
  ...rest
}) {
  const dotColors = {
    live: "var(--success)",
    idle: "var(--warning)",
    error: "var(--danger)",
    neutral: "var(--text-muted)"
  };
  return /*#__PURE__*/React.createElement("span", _extends({
    style: {
      display: "inline-flex",
      alignItems: "center",
      gap: "7px",
      minHeight: "22px",
      padding: "0 8px",
      border: "1px solid var(--border-hairline)",
      borderRadius: "var(--radius-sm)",
      background: "var(--surface-panel)",
      color: "var(--text-muted)",
      fontFamily: "var(--font-mono)",
      fontSize: "var(--text-xs)",
      whiteSpace: "nowrap",
      ...style
    }
  }, rest), dot ? /*#__PURE__*/React.createElement("span", {
    style: {
      width: "7px",
      height: "7px",
      borderRadius: "50%",
      background: dotColors[dot] || dotColors.neutral
    }
  }) : null, children);
}
Object.assign(__ds_scope, { Pill });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/feedback/Pill.jsx", error: String((e && e.message) || e) }); }

// components/feedback/StatusLine.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * StatusLine — a label/value row used throughout panels and settings
 * status stacks (muted label left, strong value right).
 */
function StatusLine({
  label,
  value,
  valueColor,
  style,
  ...rest
}) {
  return /*#__PURE__*/React.createElement("div", _extends({
    style: {
      display: "flex",
      alignItems: "center",
      justifyContent: "space-between",
      gap: "10px",
      color: "var(--text-muted)",
      fontFamily: "var(--font-mono)",
      fontSize: "var(--text-sm)",
      ...style
    }
  }, rest), /*#__PURE__*/React.createElement("span", null, label), /*#__PURE__*/React.createElement("strong", {
    style: {
      color: valueColor || "var(--text-body)",
      fontWeight: "var(--weight-strong)"
    }
  }, value));
}
Object.assign(__ds_scope, { StatusLine });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/feedback/StatusLine.jsx", error: String((e && e.message) || e) }); }

// components/forms/Button.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * Verbeam Button.
 * Flat workbench button: hairline border, raised panel-2 fill, subtle
 * white hover wash. Variants: primary (solid blue), secondary (default),
 * ghost (transparent), danger (solid red).
 */
function Button({
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
  const heights = {
    sm: "28px",
    md: "34px"
  };
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
    transition: "var(--transition-control)"
  };
  const variants = {
    primary: {
      border: "1px solid transparent",
      background: "var(--accent)",
      color: "#ffffff"
    },
    secondary: {},
    ghost: {
      border: "1px solid transparent",
      background: "transparent",
      color: "var(--text-muted)"
    },
    danger: {
      border: "1px solid transparent",
      background: "var(--danger)",
      color: "#ffffff"
    }
  };
  const [hover, setHover] = React.useState(false);
  const hoverStyles = !disabled && hover ? hoverFor(variant) : null;
  return /*#__PURE__*/React.createElement("button", _extends({
    type: type,
    disabled: disabled,
    onMouseEnter: () => setHover(true),
    onMouseLeave: () => setHover(false),
    style: {
      ...base,
      ...variants[variant],
      ...hoverStyles,
      ...style
    }
  }, rest), commandKey ? /*#__PURE__*/React.createElement("span", {
    style: {
      color: "var(--success)",
      fontFamily: "var(--font-mono)",
      fontWeight: 700
    }
  }, commandKey) : null, icon, children ? /*#__PURE__*/React.createElement("span", null, children) : null);
}
function hoverFor(variant) {
  switch (variant) {
    case "primary":
      return {
        background: "var(--accent-hover)"
      };
    case "ghost":
      return {
        background: "var(--surface-hover)",
        color: "var(--text-body)"
      };
    case "danger":
      return {
        background: "var(--danger-hover)"
      };
    default:
      return {
        background: "var(--surface-hover)",
        borderColor: "var(--border-hover)"
      };
  }
}
Object.assign(__ds_scope, { Button });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Button.jsx", error: String((e && e.message) || e) }); }

// components/forms/Field.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * Field — a mono label stacked above a control. The workbench's
 * universal form-row wrapper.
 */
function Field({
  label,
  htmlFor,
  hint,
  children,
  style,
  ...rest
}) {
  return /*#__PURE__*/React.createElement("div", _extends({
    style: {
      display: "grid",
      gap: "6px",
      minWidth: 0,
      ...style
    }
  }, rest), label ? /*#__PURE__*/React.createElement("label", {
    htmlFor: htmlFor,
    style: {
      fontFamily: "var(--font-mono)",
      fontSize: "var(--text-xs)",
      fontWeight: "var(--weight-bold)",
      color: "var(--text-label)"
    }
  }, label) : null, children, hint ? /*#__PURE__*/React.createElement("div", {
    style: {
      color: "var(--text-muted)",
      fontSize: "var(--text-sm)",
      lineHeight: "var(--leading-normal)"
    }
  }, hint) : null);
}
Object.assign(__ds_scope, { Field });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Field.jsx", error: String((e && e.message) || e) }); }

// components/forms/Input.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * LocalTranslateHub text input. Dark field, blue focus ring.
 * Supports single-line (default) and multiline (textarea, mono).
 */
function Input({
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
    transition: "var(--transition-control)"
  };
  if (multiline) {
    return /*#__PURE__*/React.createElement("textarea", _extends({
      rows: rows,
      onFocus: () => setFocused(true),
      onBlur: () => setFocused(false),
      style: {
        ...shared,
        minHeight: "126px",
        padding: "10px",
        fontSize: "12.5px",
        lineHeight: "var(--leading-relaxed)",
        resize: "vertical",
        ...style
      }
    }, rest));
  }
  return /*#__PURE__*/React.createElement("input", _extends({
    onFocus: () => setFocused(true),
    onBlur: () => setFocused(false),
    style: {
      ...shared,
      minHeight: "34px",
      padding: "0 12px",
      fontSize: "var(--text-base)",
      ...style
    }
  }, rest));
}
Object.assign(__ds_scope, { Input });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Input.jsx", error: String((e && e.message) || e) }); }

// components/forms/Select.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * LocalTranslateHub select dropdown. Matches the dark field styling.
 */
function Select({
  options = [],
  value,
  onChange,
  invalid = false,
  style,
  children,
  ...rest
}) {
  const [focused, setFocused] = React.useState(false);
  return /*#__PURE__*/React.createElement("div", {
    style: {
      position: "relative",
      width: "100%"
    }
  }, /*#__PURE__*/React.createElement("select", _extends({
    value: value,
    onChange: onChange,
    onFocus: () => setFocused(true),
    onBlur: () => setFocused(false),
    style: {
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
      ...style
    }
  }, rest), children || options.map(opt => {
    const o = typeof opt === "string" ? {
      value: opt,
      label: opt
    } : opt;
    return /*#__PURE__*/React.createElement("option", {
      key: o.value,
      value: o.value,
      disabled: o.disabled
    }, o.label ?? o.value);
  })), /*#__PURE__*/React.createElement("span", {
    "aria-hidden": "true",
    style: {
      position: "absolute",
      right: "10px",
      top: "50%",
      transform: "translateY(-50%)",
      color: "var(--text-muted)",
      fontSize: "10px",
      pointerEvents: "none"
    }
  }, "\u25BE"));
}
Object.assign(__ds_scope, { Select });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Select.jsx", error: String((e && e.message) || e) }); }

// components/navigation/NavButton.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * NavButton — a full-width sidebar navigation item. Ghost by default,
 * filled grey when active. Used for the workspace nav and settings nav.
 */
function NavButton({
  children,
  active = false,
  style,
  ...rest
}) {
  const [hover, setHover] = React.useState(false);
  const bg = active ? "var(--vb-active-bg)" : hover ? "rgba(255,255,255,0.05)" : "transparent";
  const color = active ? "var(--accent)" : hover ? "var(--text-strong)" : "#969696";
  return /*#__PURE__*/React.createElement("button", _extends({
    type: "button",
    onMouseEnter: () => setHover(true),
    onMouseLeave: () => setHover(false),
    style: {
      display: "inline-flex",
      alignItems: "center",
      justifyContent: "flex-start",
      gap: "8px",
      width: "100%",
      minHeight: "34px",
      padding: "0 12px",
      border: "1px solid transparent",
      borderRadius: "var(--radius-md)",
      background: bg,
      color,
      boxShadow: active ? "var(--ring-active)" : "none",
      fontFamily: "var(--font-sans)",
      fontSize: "var(--text-base)",
      fontWeight: active ? "var(--weight-strong)" : "var(--weight-normal)",
      cursor: "pointer",
      transition: "var(--transition-control)",
      ...style
    }
  }, rest), children);
}
Object.assign(__ds_scope, { NavButton });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/navigation/NavButton.jsx", error: String((e && e.message) || e) }); }

// components/navigation/Tab.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * Tab — a compact lowercase mode tab used in the composer (text / ocr /
 * pipe / audio / region / settings). Ghost until active.
 */
function Tab({
  children,
  active = false,
  style,
  ...rest
}) {
  const [hover, setHover] = React.useState(false);
  const bg = active ? "var(--vb-active-bg)" : hover ? "rgba(255,255,255,0.05)" : "transparent";
  const color = active ? "var(--accent)" : hover ? "var(--text-strong)" : "#969696";
  return /*#__PURE__*/React.createElement("button", _extends({
    type: "button",
    onMouseEnter: () => setHover(true),
    onMouseLeave: () => setHover(false),
    style: {
      display: "inline-flex",
      alignItems: "center",
      justifyContent: "center",
      minHeight: "34px",
      padding: "0 12px",
      border: "1px solid transparent",
      borderRadius: "var(--radius-md)",
      background: bg,
      color,
      boxShadow: active ? "var(--ring-active)" : "none",
      fontFamily: "var(--font-sans)",
      fontSize: "var(--text-sm)",
      fontWeight: active ? "var(--weight-strong)" : "var(--weight-normal)",
      cursor: "pointer",
      whiteSpace: "nowrap",
      transition: "var(--transition-control)",
      ...style
    }
  }, rest), children);
}
Object.assign(__ds_scope, { Tab });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/navigation/Tab.jsx", error: String((e && e.message) || e) }); }

// components/surfaces/Metric.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * Metric — a runtime read-out row in the inspector: a RemixIcon glyph,
 * a flexible mono label, and a strong right-aligned value.
 */
function Metric({
  icon = "ri-server-line",
  label,
  value = "-",
  style,
  ...rest
}) {
  return /*#__PURE__*/React.createElement("div", _extends({
    style: {
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
      ...style
    }
  }, rest), /*#__PURE__*/React.createElement("i", {
    className: icon,
    style: {
      fontSize: "14px",
      color: "var(--text-muted)",
      flexShrink: 0
    }
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      flex: 1,
      marginLeft: "4px"
    }
  }, label), /*#__PURE__*/React.createElement("strong", {
    style: {
      color: "#f1f5f9",
      fontWeight: "var(--weight-strong)"
    }
  }, value));
}
Object.assign(__ds_scope, { Metric });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/surfaces/Metric.jsx", error: String((e && e.message) || e) }); }

// components/surfaces/Panel.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * Panel — a titled surface card used in the inspector and elsewhere.
 * A title bar (with optional trailing action) over a padded body.
 */
function Panel({
  title,
  action,
  children,
  bodyStyle,
  style,
  ...rest
}) {
  return /*#__PURE__*/React.createElement("section", _extends({
    style: {
      border: "1px solid var(--border-hairline)",
      borderRadius: "var(--radius-lg)",
      background: "#171717",
      overflow: "hidden",
      ...style
    }
  }, rest), title ? /*#__PURE__*/React.createElement("div", {
    style: {
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
      fontWeight: "var(--weight-bold)"
    }
  }, /*#__PURE__*/React.createElement("span", null, title), action) : null, /*#__PURE__*/React.createElement("div", {
    style: {
      display: "grid",
      gap: "12px",
      padding: "12px",
      ...bodyStyle
    }
  }, children));
}
Object.assign(__ds_scope, { Panel });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/surfaces/Panel.jsx", error: String((e && e.message) || e) }); }

// components/surfaces/SettingsRow.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/**
 * SettingsRow — the settings-v2 row: a left copy block (title + muted
 * description) and a right-aligned control slot, divided by a hairline.
 */
function SettingsRow({
  title,
  description,
  control,
  last = false,
  style,
  ...rest
}) {
  return /*#__PURE__*/React.createElement("div", _extends({
    style: {
      display: "flex",
      alignItems: "flex-start",
      justifyContent: "space-between",
      gap: "24px",
      padding: "16px 0",
      borderBottom: last ? "none" : "1px solid var(--border-hairline)",
      ...style
    }
  }, rest), /*#__PURE__*/React.createElement("div", {
    style: {
      display: "grid",
      gap: "4px",
      maxWidth: "52%"
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      color: "var(--text-body)",
      fontSize: "var(--text-md)",
      fontWeight: "var(--weight-strong)"
    }
  }, title), description ? /*#__PURE__*/React.createElement("div", {
    style: {
      color: "var(--text-muted)",
      fontSize: "var(--text-sm)",
      lineHeight: "var(--leading-normal)"
    }
  }, description) : null), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: "0 0 auto",
      minWidth: "200px",
      maxWidth: "44%",
      display: "grid",
      gap: "8px"
    }
  }, control));
}
Object.assign(__ds_scope, { SettingsRow });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/surfaces/SettingsRow.jsx", error: String((e && e.message) || e) }); }

// ui_kits/workbench/Chrome.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/* global React */
const {
  useState
} = React;
const {
  Pill
} = window.LocalTranslateHubDesignSystem_32566a;

/* The fixed app chrome: topbar, activity rail, footer. Pure presentation. */

function Topbar({
  health = "ok",
  broadcast = "broadcast",
  live = false
}) {
  return /*#__PURE__*/React.createElement("header", {
    style: tbStyles.bar
  }, /*#__PURE__*/React.createElement("div", {
    style: tbStyles.brand
  }, /*#__PURE__*/React.createElement("span", {
    style: tbStyles.mark
  }, "OC"), /*#__PURE__*/React.createElement("span", null, "Verbeam")), /*#__PURE__*/React.createElement("div", {
    style: tbStyles.actions
  }, /*#__PURE__*/React.createElement(Pill, {
    dot: live ? "live" : "idle"
  }, broadcast), /*#__PURE__*/React.createElement(Pill, null, health)));
}
const tbStyles = {
  bar: {
    gridColumn: "1 / -1",
    display: "grid",
    gridTemplateColumns: "1fr auto 1fr",
    alignItems: "center",
    height: "44px",
    padding: "0 12px",
    borderBottom: "1px solid var(--vb-line)",
    background: "#0d0d0d"
  },
  brand: {
    gridColumn: 2,
    display: "inline-flex",
    alignItems: "center",
    gap: "8px",
    color: "#f1f1f1",
    fontFamily: "var(--font-mono)",
    fontSize: "12px",
    fontWeight: 650
  },
  mark: {
    color: "var(--vb-muted)",
    fontSize: "11px",
    fontWeight: 650
  },
  actions: {
    gridColumn: 3,
    justifySelf: "end",
    display: "flex",
    alignItems: "center",
    gap: "6px"
  }
};
function ActivityRail({
  route = "/app"
}) {
  const links = [{
    icon: "ri-terminal-box-line",
    href: "/app",
    url: "../workbench/index.html",
    title: "Workbench"
  }, {
    icon: "ri-broadcast-line",
    href: "/viewer",
    url: "../viewer/index.html",
    title: "Viewer"
  }, {
    icon: "ri-projector-line",
    href: "/projector",
    url: "../projector/index.html",
    title: "Projector"
  }];
  return /*#__PURE__*/React.createElement("aside", {
    style: railStyles.rail
  }, /*#__PURE__*/React.createElement("div", {
    style: railStyles.group
  }, /*#__PURE__*/React.createElement("div", {
    style: railStyles.badge
  }, "LTH"), links.map(l => /*#__PURE__*/React.createElement(RailLink, _extends({
    key: l.href
  }, l, {
    active: l.href === route
  })))), /*#__PURE__*/React.createElement("div", {
    style: railStyles.group
  }, /*#__PURE__*/React.createElement(RailLink, {
    icon: "ri-pulse-line",
    href: "/health",
    title: "Health"
  })));
}
function RailLink({
  icon,
  title,
  active,
  url
}) {
  const [hover, setHover] = useState(false);
  return /*#__PURE__*/React.createElement("a", {
    href: url || "#",
    title: title,
    onClick: e => {
      if (!url || active) e.preventDefault();
    },
    onMouseEnter: () => setHover(true),
    onMouseLeave: () => setHover(false),
    style: {
      ...railStyles.link,
      borderColor: active || hover ? "var(--vb-line-strong)" : "transparent",
      background: active || hover ? "var(--vb-rail-active)" : "transparent",
      color: active || hover ? "var(--vb-text)" : "var(--vb-muted)"
    }
  }, /*#__PURE__*/React.createElement("i", {
    className: icon
  }));
}
const railStyles = {
  rail: {
    gridColumn: 1,
    gridRow: "2 / 3",
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "space-between",
    padding: "12px 8px",
    borderRight: "1px solid var(--vb-line)",
    background: "var(--vb-rail)"
  },
  group: {
    display: "grid",
    gap: "12px",
    justifyItems: "center",
    width: "100%"
  },
  badge: {
    display: "inline-grid",
    placeItems: "center",
    minHeight: "28px",
    padding: "0 8px",
    border: "1px solid var(--vb-line)",
    borderRadius: "6px",
    background: "#161616",
    color: "var(--vb-text)",
    fontFamily: "var(--font-mono)",
    fontSize: "10px",
    fontWeight: 650
  },
  link: {
    display: "inline-grid",
    placeItems: "center",
    width: "36px",
    height: "36px",
    border: "1px solid transparent",
    borderRadius: "6px",
    fontSize: "18px",
    textDecoration: "none",
    transition: "all 0.2s ease"
  }
};
function Footer({
  left = "ready",
  right = "/app"
}) {
  return /*#__PURE__*/React.createElement("footer", {
    style: ftStyles.footer
  }, /*#__PURE__*/React.createElement("span", null, left), /*#__PURE__*/React.createElement("span", null, right));
}
const ftStyles = {
  footer: {
    gridColumn: "1 / -1",
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    height: "30px",
    padding: "0 14px",
    borderTop: "1px solid var(--vb-line)",
    background: "#0d0d0d",
    color: "var(--vb-muted)",
    fontFamily: "var(--font-mono)",
    fontSize: "11px"
  }
};
Object.assign(window, {
  VbTopbar: Topbar,
  VbActivityRail: ActivityRail,
  VbFooter: Footer
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/workbench/Chrome.jsx", error: String((e && e.message) || e) }); }

// ui_kits/workbench/Composer.jsx
try { (() => {
/* global React */
const {
  Message,
  Tab,
  Button,
  Field,
  Input,
  Pill
} = window.LocalTranslateHubDesignSystem_32566a;
function Terminal({
  messages
}) {
  const ref = React.useRef(null);
  React.useEffect(() => {
    if (ref.current) ref.current.scrollTop = ref.current.scrollHeight;
  }, [messages]);
  return /*#__PURE__*/React.createElement("section", {
    ref: ref,
    style: tStyles.terminal,
    "aria-live": "polite"
  }, messages.map(m => /*#__PURE__*/React.createElement(Message, {
    key: m.id,
    kind: m.kind,
    title: m.title,
    meta: m.meta
  }, m.body)));
}
const tStyles = {
  terminal: {
    overflow: "auto",
    padding: "16px",
    display: "grid",
    gap: "12px",
    alignContent: "start",
    background: "var(--vb-bg)"
  }
};
const MODES = [{
  id: "text",
  label: "text"
}, {
  id: "ocr",
  label: "ocr"
}, {
  id: "pipe",
  label: "pipe"
}, {
  id: "audio",
  label: "audio"
}, {
  id: "audioPipe",
  label: "audio pipe"
}, {
  id: "region",
  label: "region"
}];

/* Per-mode input panes, mirroring AppWorkbenchPage.cs
   (#textPane, #ocrPane, #audioPane, #regionPane). */

function TextPane({
  source,
  onSource,
  result
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: cStyles.grid
  }, /*#__PURE__*/React.createElement(Field, {
    label: "source",
    htmlFor: "src"
  }, /*#__PURE__*/React.createElement(Input, {
    id: "src",
    multiline: true,
    value: source,
    onChange: e => onSource(e.target.value),
    rows: 3
  })), /*#__PURE__*/React.createElement(Field, {
    label: "result",
    htmlFor: "res"
  }, /*#__PURE__*/React.createElement(Input, {
    id: "res",
    multiline: true,
    readOnly: true,
    value: result,
    rows: 3,
    placeholder: ""
  })));
}
function OcrPane() {
  return /*#__PURE__*/React.createElement("div", {
    style: cStyles.grid
  }, /*#__PURE__*/React.createElement(Field, {
    label: "image"
  }, /*#__PURE__*/React.createElement("div", {
    style: cStyles.dropZone,
    tabIndex: 0,
    role: "button"
  }, /*#__PURE__*/React.createElement("i", {
    className: "ri-image-add-line",
    style: cStyles.dropIcon
  }), /*#__PURE__*/React.createElement("div", {
    style: cStyles.dropTitle
  }, "Drop, paste, or choose an image"), /*#__PURE__*/React.createElement("div", {
    style: cStyles.dropHint
  }, "Image input auto-runs OCR. Edit the text before translating."))), /*#__PURE__*/React.createElement(Field, {
    label: "ocr text",
    htmlFor: "ocrOut"
  }, /*#__PURE__*/React.createElement(Input, {
    id: "ocrOut",
    multiline: true,
    rows: 4,
    placeholder: ""
  })));
}
function AudioPane() {
  return /*#__PURE__*/React.createElement("div", {
    style: cStyles.grid
  }, /*#__PURE__*/React.createElement(Field, {
    label: "audio source",
    htmlFor: "audUrl"
  }, /*#__PURE__*/React.createElement("div", {
    style: cStyles.stack
  }, /*#__PURE__*/React.createElement("div", {
    style: cStyles.fileRow
  }, /*#__PURE__*/React.createElement(Input, {
    id: "audUrl",
    mono: true,
    placeholder: "https:// \u2026 or choose a file",
    style: {
      flex: 1
    }
  }), /*#__PURE__*/React.createElement(Button, null, "Choose file")), /*#__PURE__*/React.createElement("span", {
    style: cStyles.hint
  }, "audio / video \xB7 auto-runs ASR"))), /*#__PURE__*/React.createElement(Field, {
    label: "asr text",
    htmlFor: "asrOut"
  }, /*#__PURE__*/React.createElement("div", {
    style: cStyles.stack
  }, /*#__PURE__*/React.createElement(Input, {
    id: "asrOut",
    multiline: true,
    rows: 2,
    placeholder: ""
  }), /*#__PURE__*/React.createElement("div", {
    style: cStyles.fileRow
  }, /*#__PURE__*/React.createElement(Button, null, "Copy SRT"), /*#__PURE__*/React.createElement(Button, null, "Copy VTT")))));
}
function RegionPane() {
  return /*#__PURE__*/React.createElement("div", {
    style: cStyles.stack
  }, /*#__PURE__*/React.createElement("div", {
    style: cStyles.toolbar
  }, /*#__PURE__*/React.createElement(Button, null, "Capture Screen"), /*#__PURE__*/React.createElement(Button, {
    variant: "primary"
  }, "Snapshot Translate"), /*#__PURE__*/React.createElement(Button, null, "Loop off"), /*#__PURE__*/React.createElement("div", {
    style: cStyles.interval
  }, /*#__PURE__*/React.createElement("span", {
    className: "vb-label"
  }, "interval ms"), /*#__PURE__*/React.createElement(Input, {
    mono: true,
    type: "number",
    defaultValue: "1500",
    style: {
      width: "90px"
    }
  }))), /*#__PURE__*/React.createElement("div", {
    style: cStyles.regionStage,
    tabIndex: 0
  }, "Capture a screen or window, then drag a box over the dialogue area."), /*#__PURE__*/React.createElement("div", {
    style: cStyles.grid
  }, /*#__PURE__*/React.createElement(Field, {
    label: "region ocr",
    htmlFor: "regOcr"
  }, /*#__PURE__*/React.createElement(Input, {
    id: "regOcr",
    multiline: true,
    rows: 2,
    placeholder: ""
  })), /*#__PURE__*/React.createElement(Field, {
    label: "region translation",
    htmlFor: "regTr"
  }, /*#__PURE__*/React.createElement(Input, {
    id: "regTr",
    multiline: true,
    readOnly: true,
    rows: 2,
    placeholder: ""
  }))));
}
function Composer({
  mode,
  onMode,
  source,
  onSource,
  result,
  latency,
  onRun,
  onClear
}) {
  return /*#__PURE__*/React.createElement("section", {
    style: cStyles.composer
  }, /*#__PURE__*/React.createElement("div", {
    style: cStyles.tabs
  }, MODES.map(m => /*#__PURE__*/React.createElement(Tab, {
    key: m.id,
    active: mode === m.id,
    onClick: () => onMode(m.id)
  }, m.label))), mode === "text" && /*#__PURE__*/React.createElement(TextPane, {
    source: source,
    onSource: onSource,
    result: result
  }), (mode === "ocr" || mode === "pipe") && /*#__PURE__*/React.createElement(OcrPane, null), (mode === "audio" || mode === "audioPipe") && /*#__PURE__*/React.createElement(AudioPane, null), mode === "region" && /*#__PURE__*/React.createElement(RegionPane, null), /*#__PURE__*/React.createElement("div", {
    style: cStyles.actions
  }, /*#__PURE__*/React.createElement("div", {
    style: cStyles.left
  }, /*#__PURE__*/React.createElement(Button, {
    variant: "primary",
    commandKey: ">",
    onClick: onRun
  }, "Run"), mode === "ocr" && /*#__PURE__*/React.createElement(Button, null, "Translate OCR Text"), /*#__PURE__*/React.createElement(Button, {
    onClick: onClear
  }, "Clear")), /*#__PURE__*/React.createElement("div", {
    style: cStyles.right
  }, latency > 0 && /*#__PURE__*/React.createElement(Pill, null, latency, " ms"))));
}
const cStyles = {
  composer: {
    borderTop: "1px solid var(--vb-line)",
    background: "var(--vb-panel)",
    padding: "12px 16px 16px",
    display: "grid",
    gap: "12px"
  },
  tabs: {
    display: "flex",
    flexWrap: "wrap",
    gap: "4px"
  },
  grid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: "12px"
  },
  stack: {
    display: "grid",
    gap: "10px",
    alignContent: "start"
  },
  actions: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "10px"
  },
  left: {
    display: "flex",
    gap: "8px"
  },
  right: {
    display: "flex",
    gap: "6px"
  },
  dropZone: {
    display: "grid",
    justifyItems: "center",
    alignContent: "center",
    gap: "4px",
    minHeight: "118px",
    padding: "14px",
    border: "1px dashed var(--vb-line-strong)",
    borderRadius: "6px",
    background: "var(--vb-layer-2)",
    cursor: "pointer",
    textAlign: "center"
  },
  dropIcon: {
    color: "var(--vb-muted)",
    fontSize: "20px"
  },
  dropTitle: {
    color: "var(--vb-text)",
    fontSize: "13px",
    fontWeight: 500
  },
  dropHint: {
    color: "var(--vb-muted)",
    fontSize: "11px"
  },
  fileRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px"
  },
  hint: {
    color: "var(--vb-muted)",
    fontSize: "11px",
    fontFamily: "var(--font-mono)"
  },
  toolbar: {
    display: "flex",
    flexWrap: "wrap",
    alignItems: "center",
    gap: "8px"
  },
  interval: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    marginLeft: "auto"
  },
  regionStage: {
    display: "grid",
    placeItems: "center",
    minHeight: "84px",
    padding: "12px",
    border: "1px solid var(--vb-line)",
    borderRadius: "6px",
    background: "var(--vb-bg)",
    color: "var(--vb-muted)",
    fontSize: "12px",
    textAlign: "center"
  }
};
Object.assign(window, {
  VbTerminal: Terminal,
  VbComposer: Composer
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/workbench/Composer.jsx", error: String((e && e.message) || e) }); }

// ui_kits/workbench/SettingsScreen.jsx
try { (() => {
/* global React */
const {
  useState
} = React;
const {
  SettingsRow,
  Field,
  Select,
  Input,
  Notice,
  StatusLine
} = window.LocalTranslateHubDesignSystem_32566a;
const NAV = [{
  group: "desktop",
  items: [["general", "General"], ["providers", "Providers"], ["sound", "Sound"]]
}, {
  group: "pipelines",
  items: [["ocr", "OCR"], ["audio", "Audio"], ["region", "Region"]]
}, {
  group: "runtime",
  items: [["broadcast", "Broadcast"]]
}];
function SettingsScreen() {
  const [section, setSection] = useState("general");
  return /*#__PURE__*/React.createElement("section", {
    style: stStyles.pane,
    "aria-label": "Settings"
  }, /*#__PURE__*/React.createElement("nav", {
    style: stStyles.nav
  }, NAV.map(g => /*#__PURE__*/React.createElement("div", {
    key: g.group,
    style: stStyles.navGroup
  }, /*#__PURE__*/React.createElement("div", {
    className: "vb-label",
    style: stStyles.navTitle
  }, g.group), g.items.map(([id, label]) => /*#__PURE__*/React.createElement("button", {
    key: id,
    type: "button",
    onClick: () => setSection(id),
    style: {
      ...stStyles.navBtn,
      background: section === id ? "var(--vb-active-bg)" : "transparent",
      color: section === id ? "var(--vb-blue)" : "#969696",
      boxShadow: section === id ? "var(--ring-active)" : "none",
      fontWeight: section === id ? 600 : 400
    }
  }, label)))), /*#__PURE__*/React.createElement("div", {
    style: stStyles.navFooter
  }, /*#__PURE__*/React.createElement("span", null, "Verbeam"), /*#__PURE__*/React.createElement("span", null, "OpenCode desktop"))), /*#__PURE__*/React.createElement("div", {
    style: stStyles.body
  }, SECTIONS[section]()));
}
function SectionBlock({
  title,
  children
}) {
  return /*#__PURE__*/React.createElement("section", {
    style: {
      display: "flex",
      flexDirection: "column",
      gap: "0"
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: stStyles.sectionTitle
  }, title), children);
}
const SECTIONS = {
  general: () => /*#__PURE__*/React.createElement(SectionBlock, {
    title: "Translation defaults"
  }, /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Language pair",
    description: "Default source and target for text, OCR, audio, and region pipelines.",
    control: /*#__PURE__*/React.createElement("div", {
      style: {
        display: "grid",
        gridTemplateColumns: "1fr 1fr",
        gap: "8px"
      }
    }, /*#__PURE__*/React.createElement(Field, {
      label: "source"
    }, /*#__PURE__*/React.createElement(Select, {
      options: ["ja", "en", "ko", "zh"],
      defaultValue: "ja"
    })), /*#__PURE__*/React.createElement(Field, {
      label: "target"
    }, /*#__PURE__*/React.createElement(Select, {
      options: ["zh-TW", "zh-CN", "en", "ja"],
      defaultValue: "zh-TW"
    })))
  }), /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Prompt mode",
    description: "Prompt preset used when the translation request does not override mode.",
    control: /*#__PURE__*/React.createElement(Field, {
      label: "mode"
    }, /*#__PURE__*/React.createElement(Select, {
      options: ["faithful", "natural", "literal"],
      defaultValue: "faithful"
    }))
  }), /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Glossary",
    description: "Optional local glossary file applied to translation requests.",
    last: true,
    control: /*#__PURE__*/React.createElement(Field, {
      label: "glossary"
    }, /*#__PURE__*/React.createElement(Select, {
      options: ["none", "game-terms.csv", "honorifics.csv"],
      defaultValue: "game-terms.csv"
    }))
  })),
  providers: () => /*#__PURE__*/React.createElement(SectionBlock, {
    title: "Translation provider"
  }, /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Provider",
    description: "Runtime used by text, OCR translate, audio translate, and region translate.",
    control: /*#__PURE__*/React.createElement(Field, {
      label: "provider"
    }, /*#__PURE__*/React.createElement(Select, {
      options: ["ollama", "lmstudio", "openai-compatible"],
      defaultValue: "ollama"
    }))
  }), /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Model",
    description: "Installed or configured model for the selected provider.",
    control: /*#__PURE__*/React.createElement(Field, {
      label: "model"
    }, /*#__PURE__*/React.createElement(Input, {
      mono: true,
      defaultValue: "verbeam-mort-qwen2.5-0.5b:latest"
    }))
  }), /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Model recommendation",
    description: "Best configured choice for the selected provider and local workload.",
    last: true,
    control: /*#__PURE__*/React.createElement("div", {
      style: stStyles.stack
    }, /*#__PURE__*/React.createElement(StatusLine, {
      label: "recommended",
      value: "verbeam-mort-qwen2.5-0.5b:latest"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "use",
      value: "general"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "reason",
      value: "balanced ja\u2192zh"
    }))
  })),
  sound: () => /*#__PURE__*/React.createElement(SectionBlock, {
    title: "Sound effects"
  }, /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Enable sounds",
    description: "Play OpenCode-style feedback sounds for clicks, success, errors, and notifications.",
    last: true,
    control: /*#__PURE__*/React.createElement(Field, {
      label: "state"
    }, /*#__PURE__*/React.createElement(Select, {
      options: ["enabled", "disabled"],
      defaultValue: "enabled"
    }))
  })),
  ocr: () => /*#__PURE__*/React.createElement(SectionBlock, {
    title: "Recognition"
  }, /*#__PURE__*/React.createElement(SettingsRow, {
    title: "OCR engine",
    description: "Engine used to read text from images, screenshots and screen regions.",
    control: /*#__PURE__*/React.createElement(Field, {
      label: "engine"
    }, /*#__PURE__*/React.createElement(Select, {
      options: ["windows-ocr", "tesseract", "easyocr", "paddleocr"],
      defaultValue: "windows-ocr"
    }))
  }), /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Engine status",
    description: "Availability of the selected recognition engine.",
    last: true,
    control: /*#__PURE__*/React.createElement("div", {
      style: stStyles.stack
    }, /*#__PURE__*/React.createElement(Notice, null, "\u9810\u8A2D\u672C\u6A5F OCR\uFF0C\u8F15\u91CF\uFF0C\u9069\u5408\u4E00\u822C\u6587\u5B57"), /*#__PURE__*/React.createElement(StatusLine, {
      label: "available",
      value: "yes",
      valueColor: "var(--success)"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "source",
      value: "windows"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "blocks",
      value: "12"
    }))
  })),
  audio: () => /*#__PURE__*/React.createElement(SectionBlock, {
    title: "Speech recognition"
  }, /*#__PURE__*/React.createElement(SettingsRow, {
    title: "ASR engine",
    description: "Engine used to transcribe audio and video into source text.",
    control: /*#__PURE__*/React.createElement(Field, {
      label: "engine"
    }, /*#__PURE__*/React.createElement(Select, {
      options: ["whisper-local", "whisper-cpp", "vosk"],
      defaultValue: "whisper-local"
    }))
  }), /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Engine status",
    description: "Availability of the selected speech engine.",
    last: true,
    control: /*#__PURE__*/React.createElement("div", {
      style: stStyles.stack
    }, /*#__PURE__*/React.createElement(StatusLine, {
      label: "available",
      value: "yes",
      valueColor: "var(--success)"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "segments",
      value: "0"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "engine",
      value: "whisper-local"
    }))
  })),
  region: () => /*#__PURE__*/React.createElement(SectionBlock, {
    title: "Screen capture"
  }, /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Loop interval",
    description: "Milliseconds between automatic region snapshots.",
    control: /*#__PURE__*/React.createElement(Field, {
      label: "interval ms"
    }, /*#__PURE__*/React.createElement(Input, {
      mono: true,
      type: "number",
      defaultValue: "1500"
    }))
  }), /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Capture status",
    description: "Current screen-capture and loop state.",
    last: true,
    control: /*#__PURE__*/React.createElement("div", {
      style: stStyles.stack
    }, /*#__PURE__*/React.createElement(StatusLine, {
      label: "capture",
      value: "off"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "loop",
      value: "off"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "selection",
      value: "\u2014"
    }))
  })),
  broadcast: () => /*#__PURE__*/React.createElement(SectionBlock, {
    title: "Latest translation"
  }, /*#__PURE__*/React.createElement(SettingsRow, {
    title: "Latest broadcast",
    description: "Most recent message pushed to the viewer and projector surfaces.",
    last: true,
    control: /*#__PURE__*/React.createElement("div", {
      style: stStyles.stack
    }, /*#__PURE__*/React.createElement(StatusLine, {
      label: "source",
      value: "ja"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "target",
      value: "zh-TW"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "kind",
      value: "text"
    }), /*#__PURE__*/React.createElement(StatusLine, {
      label: "provider",
      value: "ollama"
    }))
  }))
};
const stStyles = {
  pane: {
    display: "grid",
    gridTemplateColumns: "236px minmax(0,1fr)",
    overflow: "hidden",
    background: "var(--vb-bg)"
  },
  nav: {
    borderRight: "1px solid var(--vb-line)",
    background: "var(--vb-panel)",
    padding: "18px 14px",
    display: "flex",
    flexDirection: "column",
    gap: "20px",
    overflow: "auto"
  },
  navGroup: {
    display: "grid",
    gap: "4px"
  },
  navTitle: {
    marginBottom: "6px"
  },
  navBtn: {
    display: "flex",
    alignItems: "center",
    minHeight: "32px",
    padding: "0 12px",
    border: "1px solid transparent",
    borderRadius: "var(--radius-md)",
    background: "transparent",
    fontFamily: "var(--font-sans)",
    fontSize: "13px",
    textAlign: "left",
    cursor: "pointer",
    transition: "var(--transition-control)"
  },
  navFooter: {
    marginTop: "auto",
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    paddingTop: "14px",
    fontFamily: "var(--font-mono)",
    fontSize: "10px",
    color: "var(--vb-faint)"
  },
  body: {
    padding: "0 40px 40px",
    overflow: "auto",
    display: "flex",
    flexDirection: "column",
    gap: "36px"
  },
  sectionTitle: {
    padding: "28px 0 8px",
    color: "var(--vb-text)",
    fontSize: "15px",
    fontWeight: 640
  },
  stack: {
    display: "grid",
    gap: "7px"
  }
};
Object.assign(window, {
  VbSettingsScreen: SettingsScreen
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/workbench/SettingsScreen.jsx", error: String((e && e.message) || e) }); }

// ui_kits/workbench/Sidebar.jsx
try { (() => {
/* global React */
const {
  useState: useSbState
} = React;
const {
  NavButton,
  Metric
} = window.LocalTranslateHubDesignSystem_32566a;
const WORKSPACES = [{
  id: "translate",
  label: "Translate",
  icon: "ri-translate-2"
}, {
  id: "ocr",
  label: "OCR",
  icon: "ri-scan-2-line"
}, {
  id: "pipeline",
  label: "OCR + Translate",
  icon: "ri-bubble-chart-line"
}, {
  id: "audio",
  label: "Audio",
  icon: "ri-mic-line"
}, {
  id: "audioPipeline",
  label: "Audio + Translate",
  icon: "ri-voiceprint-line"
}, {
  id: "region",
  label: "Region",
  icon: "ri-focus-3-line"
}, {
  id: "settings",
  label: "Settings",
  icon: "ri-settings-3-line"
}];
function Sidebar({
  active = "translate",
  onSelect,
  runtime
}) {
  const [runtimeOpen, setRuntimeOpen] = useSbState(false);
  return /*#__PURE__*/React.createElement("aside", {
    style: sbStyles.sidebar
  }, /*#__PURE__*/React.createElement("div", {
    style: sbStyles.section
  }, /*#__PURE__*/React.createElement("div", {
    className: "vb-label",
    style: sbStyles.title
  }, "workspace"), /*#__PURE__*/React.createElement("div", {
    style: sbStyles.nav
  }, WORKSPACES.map(w => /*#__PURE__*/React.createElement(NavButton, {
    key: w.id,
    active: active === w.id,
    onClick: () => onSelect && onSelect(w.id)
  }, /*#__PURE__*/React.createElement("i", {
    className: w.icon,
    style: {
      fontSize: "15px",
      width: "18px",
      textAlign: "center"
    }
  }), w.label)))), /*#__PURE__*/React.createElement("div", {
    style: sbStyles.section
  }, /*#__PURE__*/React.createElement("button", {
    type: "button",
    onClick: () => setRuntimeOpen(!runtimeOpen),
    "aria-expanded": runtimeOpen,
    title: runtimeOpen ? "Hide runtime details" : "Show runtime details",
    style: sbStyles.runtimeToggle
  }, /*#__PURE__*/React.createElement("span", {
    className: "vb-label"
  }, "runtime"), /*#__PURE__*/React.createElement("span", {
    style: sbStyles.runtimeSummary
  }, runtime.model), /*#__PURE__*/React.createElement("i", {
    className: runtimeOpen ? "ri-arrow-up-s-line" : "ri-arrow-down-s-line",
    style: sbStyles.runtimeChevron
  })), runtimeOpen && /*#__PURE__*/React.createElement("div", {
    style: sbStyles.metrics
  }, /*#__PURE__*/React.createElement(Metric, {
    icon: "ri-database-2-line",
    label: "provider",
    value: runtime.provider
  }), /*#__PURE__*/React.createElement(Metric, {
    icon: "ri-cpu-line",
    label: "model",
    value: runtime.model
  }), /*#__PURE__*/React.createElement(Metric, {
    icon: "ri-file-text-line",
    label: "ocr",
    value: runtime.ocr
  }), /*#__PURE__*/React.createElement(Metric, {
    icon: "ri-sound-module-line",
    label: "asr",
    value: runtime.asr
  }), /*#__PURE__*/React.createElement(Metric, {
    icon: "ri-server-line",
    label: "cache",
    value: runtime.cache
  }))));
}
const sbStyles = {
  sidebar: {
    gridColumn: 2,
    gridRow: "2 / 3",
    borderRight: "1px solid var(--vb-line)",
    background: "var(--vb-panel)",
    display: "grid",
    gridTemplateRows: "auto auto",
    alignContent: "start",
    minWidth: 0,
    overflow: "auto"
  },
  section: {
    padding: "18px 14px",
    borderBottom: "1px solid var(--vb-line)"
  },
  title: {
    marginBottom: "12px"
  },
  nav: {
    display: "grid",
    gap: "4px"
  },
  metrics: {
    display: "grid",
    gap: "8px",
    marginTop: "12px"
  },
  runtimeToggle: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    width: "100%",
    minHeight: "28px",
    padding: "0",
    border: "none",
    background: "transparent",
    cursor: "pointer",
    textAlign: "left"
  },
  runtimeSummary: {
    marginLeft: "auto",
    color: "var(--vb-muted)",
    fontFamily: "var(--font-mono)",
    fontSize: "11px",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap"
  },
  runtimeChevron: {
    color: "var(--vb-muted)",
    fontSize: "14px",
    flex: "none"
  }
};
Object.assign(window, {
  VbSidebar: Sidebar
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/workbench/Sidebar.jsx", error: String((e && e.message) || e) }); }

// ui_kits/workbench/WorkbenchApp.jsx
try { (() => {
/* global React */
const {
  useState
} = React;

/* Fake local translation for the demo (ja → zh-TW). */
const DICTIONARY = {
  "こんにちは、勇者さん。": "你好，勇者大人。",
  "ここはどこですか？": "這裡是哪裡？",
  "魔王を倒すための旅に出よう。": "踏上討伐魔王的旅程吧。",
  "セーブしますか？": "要存檔嗎？"
};
function fakeTranslate(src) {
  const t = (src || "").trim();
  if (!t) return null;
  return DICTIONARY[t] || "（本機翻譯）" + t;
}
function now() {
  return new Date().toLocaleTimeString("en-GB", {
    hour12: false
  });
}
function WorkbenchApp() {
  const [active, setActive] = useState("translate");
  const [mode, setMode] = useState("text");
  const [source, setSource] = useState("こんにちは、勇者さん。");
  const [result, setResult] = useState("");
  const [latency, setLatency] = useState(0);
  const [live, setLive] = useState(false);
  const [footerLeft, setFooterLeft] = useState("ready");
  const [messages, setMessages] = useState([{
    id: 0,
    kind: "system",
    title: "boot",
    meta: now(),
    body: "Verbeam app ready"
  }]);
  const runtime = {
    provider: "ollama",
    model: "verbeam-mort-qwen2.5-0.5b:latest",
    ocr: "windows-ocr",
    asr: "whisper-local",
    cache: "warm"
  };
  function append(msg) {
    setMessages(prev => [...prev, {
      id: prev.length ? prev[prev.length - 1].id + 1 : 0,
      ...msg
    }]);
  }
  function handleRun() {
    if (mode !== "text") {
      const ms = 180 + Math.floor(Math.random() * 140);
      append({
        kind: "system",
        title: mode,
        meta: `${ms} ms`,
        body: `${mode} pipeline is not wired in the fallback bundle`
      });
      setLatency(ms);
      setFooterLeft(`${mode} · demo`);
      return;
    }
    const out = fakeTranslate(source);
    if (!out) {
      append({
        kind: "error",
        title: "translate",
        meta: "0 ms",
        body: "source is empty"
      });
      return;
    }
    const ms = 120 + Math.floor(Math.random() * 90);
    append({
      kind: "user",
      title: "source",
      meta: `ollama / ${runtime.model}`,
      body: source.trim()
    });
    append({
      kind: "result",
      title: "translation",
      meta: `${ms} ms`,
      body: out
    });
    setResult(out);
    setLatency(ms);
    setLive(true);
    setFooterLeft(`translated · ${ms} ms`);
  }
  function handleClear() {
    setMessages([]);
    setResult("");
    setLatency(0);
    setFooterLeft("cleared");
  }
  const showSettings = active === "settings";
  return /*#__PURE__*/React.createElement("div", {
    style: appStyles.shell
  }, /*#__PURE__*/React.createElement(VbTopbar, {
    health: "ok",
    broadcast: live ? "broadcast" : "idle",
    live: live
  }), /*#__PURE__*/React.createElement(VbActivityRail, {
    route: "/app"
  }), /*#__PURE__*/React.createElement(VbSidebar, {
    active: active,
    onSelect: setActive,
    runtime: runtime
  }), /*#__PURE__*/React.createElement("main", {
    style: appStyles.workspace
  }, showSettings ? /*#__PURE__*/React.createElement(VbSettingsScreen, null) : /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(VbTerminal, {
    messages: messages
  }), /*#__PURE__*/React.createElement(VbComposer, {
    mode: mode,
    onMode: setMode,
    source: source,
    onSource: setSource,
    result: result,
    latency: latency,
    onRun: handleRun,
    onClear: handleClear
  }))), /*#__PURE__*/React.createElement(VbFooter, {
    left: footerLeft,
    right: showSettings ? "/app · settings" : "/app"
  }));
}
const appStyles = {
  shell: {
    display: "grid",
    gridTemplateColumns: "53px 230px minmax(0,1fr)",
    gridTemplateRows: "44px minmax(0,1fr) 30px",
    height: "100vh",
    background: "var(--vb-bg)"
  },
  workspace: {
    gridColumn: 3,
    gridRow: "2 / 3",
    display: "grid",
    gridTemplateRows: "minmax(0,1fr) auto",
    minWidth: 0,
    overflow: "hidden"
  }
};

/* No side effects here — index.html mounts the app. This file is also
   compiled into _ds_bundle.js, so auto-rendering would double-mount. */
Object.assign(window, {
  VbWorkbenchApp: WorkbenchApp
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/workbench/WorkbenchApp.jsx", error: String((e && e.message) || e) }); }

__ds_ns.Message = __ds_scope.Message;

__ds_ns.Notice = __ds_scope.Notice;

__ds_ns.Pill = __ds_scope.Pill;

__ds_ns.StatusLine = __ds_scope.StatusLine;

__ds_ns.Button = __ds_scope.Button;

__ds_ns.Field = __ds_scope.Field;

__ds_ns.Input = __ds_scope.Input;

__ds_ns.Select = __ds_scope.Select;

__ds_ns.NavButton = __ds_scope.NavButton;

__ds_ns.Tab = __ds_scope.Tab;

__ds_ns.Metric = __ds_scope.Metric;

__ds_ns.Panel = __ds_scope.Panel;

__ds_ns.SettingsRow = __ds_scope.SettingsRow;

})();
