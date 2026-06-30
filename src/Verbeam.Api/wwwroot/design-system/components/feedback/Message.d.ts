import * as React from "react";

/** A terminal log entry: bordered card with a kind-tinted mono header and wrapped body. */
export interface MessageProps extends React.HTMLAttributes<HTMLElement> {
  /** Header tint. @default "result" */
  kind?: "user" | "result" | "error" | "system";
  /** Header label (left), e.g. "translation", "ocr", "boot". */
  title?: React.ReactNode;
  /** Header meta (right), e.g. timestamp, provider, "142 ms". */
  meta?: React.ReactNode;
  children?: React.ReactNode;
}

export function Message(props: MessageProps): JSX.Element;
