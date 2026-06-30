import * as React from "react";

/**
 * Flat OpenCode-style button with hairline border and subtle hover fill.
 *
 * @startingPoint section="Forms" subtitle="Primary / secondary / ghost / danger button" viewport="700x180"
 */
export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  /** Visual style. @default "secondary" */
  variant?: "primary" | "secondary" | "ghost" | "danger";
  /** Control height. @default "md" */
  size?: "sm" | "md";
  /** Leading monospace command glyph rendered in green (e.g. ">"). */
  commandKey?: string;
  /** Optional leading icon/element. */
  icon?: React.ReactNode;
  /** Stretch to fill the container width. @default false */
  fullWidth?: boolean;
  children?: React.ReactNode;
}

export function Button(props: ButtonProps): JSX.Element;
