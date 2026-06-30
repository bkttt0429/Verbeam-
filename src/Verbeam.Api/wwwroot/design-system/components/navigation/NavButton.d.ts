import * as React from "react";

/** Full-width sidebar navigation item; ghost by default, filled grey when active. */
export interface NavButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  /** Active (selected) state. @default false */
  active?: boolean;
  children?: React.ReactNode;
}

export function NavButton(props: NavButtonProps): JSX.Element;
