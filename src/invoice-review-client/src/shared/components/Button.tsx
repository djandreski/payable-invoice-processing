import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  children: ReactNode;
  tone?: 'primary' | 'secondary' | 'danger';
};

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { children, className = '', tone = 'primary', type = 'button', ...props },
  ref,
) {
  return (
    <button ref={ref} className={`button button--${tone} ${className}`.trim()} type={type} {...props}>
      {children}
    </button>
  );
});
