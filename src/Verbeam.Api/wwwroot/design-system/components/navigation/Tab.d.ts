import * as React from "react";

/** Compact lowercase mode tab for the composer; ghost until active. */
export interface TabProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  /** Active (selected) state. @default false */
  active?: boolean;
  children?: React.ReactNode;
}

export function Tab(props: TabProps): JSX.Element;
