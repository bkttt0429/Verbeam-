import * as React from "react";

/** Runtime read-out row: RemixIcon glyph + mono label + strong right value. */
export interface MetricProps extends React.HTMLAttributes<HTMLDivElement> {
  /** RemixIcon class, e.g. "ri-cpu-line". @default "ri-server-line" */
  icon?: string;
  /** Mono label, e.g. "provider", "model". */
  label?: React.ReactNode;
  /** Right-aligned value. @default "-" */
  value?: React.ReactNode;
}

export function Metric(props: MetricProps): JSX.Element;
