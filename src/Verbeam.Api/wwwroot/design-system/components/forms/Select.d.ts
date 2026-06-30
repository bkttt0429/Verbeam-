import * as React from "react";

export interface SelectOption {
  value: string;
  label?: string;
  disabled?: boolean;
}

/** Dark dropdown matching the form field styling, with a custom chevron. */
export interface SelectProps
  extends Omit<React.SelectHTMLAttributes<HTMLSelectElement>, "onChange"> {
  /** Options as strings or {value,label,disabled} objects. Ignored if children given. */
  options?: Array<string | SelectOption>;
  value?: string;
  onChange?: React.ChangeEventHandler<HTMLSelectElement>;
  /** Show invalid (red) border. @default false */
  invalid?: boolean;
}

export function Select(props: SelectProps): JSX.Element;
