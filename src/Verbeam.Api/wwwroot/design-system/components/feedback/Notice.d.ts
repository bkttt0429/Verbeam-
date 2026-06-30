import * as React from "react";

/** Inline amber advisory box for engine/provider hints. */
export interface NoticeProps extends React.HTMLAttributes<HTMLDivElement> {
  children?: React.ReactNode;
}

export function Notice(props: NoticeProps): JSX.Element;
