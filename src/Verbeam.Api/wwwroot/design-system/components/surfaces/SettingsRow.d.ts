import * as React from "react";

/** Settings-v2 row: copy block (title + muted description) left, control slot right. */
export interface SettingsRowProps extends React.HTMLAttributes<HTMLDivElement> {
  /** Row title (Sentence case). */
  title: React.ReactNode;
  /** Muted one-line description. */
  description?: React.ReactNode;
  /** The control(s) shown on the right (a Field, Select, checkbox, status stack…). */
  control?: React.ReactNode;
  /** Drop the bottom hairline (use on the last row in a section). @default false */
  last?: boolean;
}

export function SettingsRow(props: SettingsRowProps): JSX.Element;
