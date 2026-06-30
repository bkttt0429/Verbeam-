import * as React from "react";

/** Compact monospace status capsule, optionally with a leading status dot. */
export interface PillProps extends React.HTMLAttributes<HTMLSpanElement> {
  /** Leading status dot color. Omit for no dot. */
  dot?: "live" | "idle" | "error" | "neutral";
  children?: React.ReactNode;
}

export function Pill(props: PillProps): JSX.Element;
