import type { CSSProperties } from "react";

type IconName =
  | "orbit"
  | "search"
  | "layers"
  | "arrow"
  | "refresh"
  | "crosshair"
  | "clock"
  | "info"
  | "globe"
  | "chevron"
  | "signal"
  | "external";
const paths: Record<IconName, React.ReactNode> = {
  orbit: (
    <>
      <circle cx="12" cy="12" r="5" />
      <ellipse cx="12" cy="12" rx="11" ry="4" transform="rotate(-35 12 12)" />
      <circle cx="20" cy="6" r="1.5" fill="currentColor" stroke="none" />
    </>
  ),
  search: (
    <>
      <circle cx="10.5" cy="10.5" r="6.5" />
      <path d="m16 16 5 5" />
    </>
  ),
  layers: (
    <>
      <path d="m12 3 10 5-10 5L2 8l10-5Z" />
      <path d="m2 12 10 5 10-5M2 16l10 5 10-5" />
    </>
  ),
  arrow: (
    <>
      <path d="M5 12h14m-6-6 6 6-6 6" />
    </>
  ),
  refresh: (
    <>
      <path d="M20 7v5h-5M4 17v-5h5" />
      <path d="M6 7a7 7 0 0 1 12-1l2 3M4 15l2 3a7 7 0 0 0 12-1" />
    </>
  ),
  crosshair: (
    <>
      <circle cx="12" cy="12" r="7" />
      <path d="M12 2v5m0 10v5M2 12h5m10 0h5" />
      <circle cx="12" cy="12" r="1" />
    </>
  ),
  clock: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 6v6l4 2" />
    </>
  ),
  info: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v6m0-10v.5" />
    </>
  ),
  globe: (
    <>
      <circle cx="12" cy="12" r="9" />
      <ellipse cx="12" cy="12" rx="4" ry="9" />
      <path d="M3 12h18" />
    </>
  ),
  chevron: <path d="m9 5 7 7-7 7" />,
  signal: (
    <>
      <path d="M4 18v2M9 13v7m5-12v12m5-17v17" />
    </>
  ),
  external: (
    <>
      <path d="M14 3h7v7m0-7L10 14M10 3H4a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h16a1 1 0 0 0 1-1v-6" />
    </>
  ),
};

export function Icon({
  name,
  size = 18,
  className,
  style,
}: {
  name: IconName;
  size?: number;
  className?: string;
  style?: CSSProperties;
}) {
  return (
    <svg
      aria-hidden="true"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      style={style}
    >
      {paths[name]}
    </svg>
  );
}
