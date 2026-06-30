import * as React from "react";

/** A lowercase mono label stacked above a control, with optional hint text below. */
export interface FieldProps extends React.HTMLAttributes<HTMLDivElement> {
  /** Lowercase mono label shown above the control. */
  label?: React.ReactNode;
  /** `for` attribute linking the label to the control. */
  htmlFor?: string;
  /** Muted helper text rendered below the control. */
  hint?: React.ReactNode;
  children?: React.ReactNode;
}

export function Field(props: FieldProps): JSX.Element;
