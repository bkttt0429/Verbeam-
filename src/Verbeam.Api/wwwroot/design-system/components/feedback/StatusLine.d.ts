import * as React from "react";

/** A label/value status row: muted mono label left, strong value right. */
export interface StatusLineProps extends React.HTMLAttributes<HTMLDivElement> {
  /** Left-side label. */
  label: React.ReactNode;
  /** Right-side value. */
  value: React.ReactNode;
  /** Override the value color (e.g. var(--success) for "ok"). */
  valueColor?: string;
}

export function StatusLine(props: StatusLineProps): JSX.Element;
