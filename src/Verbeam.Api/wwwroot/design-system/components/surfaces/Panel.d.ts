import * as React from "react";

/** Titled surface card: a mono title bar over a padded body grid. */
export interface PanelProps extends React.HTMLAttributes<HTMLElement> {
  /** Mono title shown in the header bar. Omit for a borderless body-only card. */
  title?: React.ReactNode;
  /** Optional trailing element in the title bar (e.g. a small action). */
  action?: React.ReactNode;
  /** Style overrides for the body grid. */
  bodyStyle?: React.CSSProperties;
  children?: React.ReactNode;
}

export function Panel(props: PanelProps): JSX.Element;
