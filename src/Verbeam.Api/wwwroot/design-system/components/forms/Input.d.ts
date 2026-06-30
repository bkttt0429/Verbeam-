import * as React from "react";

/** Dark text field with blue focus ring; single-line or multiline mono textarea. */
export interface InputProps
  extends React.InputHTMLAttributes<HTMLInputElement & HTMLTextAreaElement> {
  /** Render a resizable monospace textarea instead of a single-line input. @default false */
  multiline?: boolean;
  /** Force monospace font on a single-line input (e.g. for codes/values). @default false */
  mono?: boolean;
  /** Textarea row count when multiline. @default 5 */
  rows?: number;
  /** Show invalid (red) border. @default false */
  invalid?: boolean;
}

export function Input(props: InputProps): JSX.Element;
