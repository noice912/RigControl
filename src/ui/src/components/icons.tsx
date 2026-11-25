// Simple stroke icons drawn for this app (no icon library).
const common = {
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.8,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  "aria-hidden": true,
};

export const Icons = {
  overview: () => (
    <svg {...common}>
      <path d="M4 15a8 8 0 1 1 16 0" />
      <path d="M12 15l4-5" />
      <path d="M4 19h16" />
    </svg>
  ),
  cooling: () => (
    <svg {...common}>
      <circle cx="12" cy="12" r="2" />
      <path d="M12 10c0-4 1-6 3-6s3 2 1 5" />
      <path d="M14 12c4 0 6 1 6 3s-2 3-5 1" />
      <path d="M12 14c0 4-1 6-3 6s-3-2-1-5" />
      <path d="M10 12c-4 0-6-1-6-3s2-3 5-1" />
    </svg>
  ),
  graphics: () => (
    <svg {...common}>
      <rect x="3" y="7" width="18" height="11" rx="2" />
      <circle cx="9" cy="12.5" r="2.5" />
      <circle cx="16" cy="12.5" r="2.5" />
      <path d="M6 18v2M10 18v2M14 18v2M18 18v2" />
    </svg>
  ),
  lighting: () => (
    <svg {...common}>
      <path d="M9 18h6" />
      <path d="M10 21h4" />
      <path d="M12 3a6 6 0 0 0-4 10.5c.7.7 1 1.5 1 2.5h6c0-1 .3-1.8 1-2.5A6 6 0 0 0 12 3z" />
    </svg>
  ),
  drivers: () => (
    <svg {...common}>
      <rect x="6" y="6" width="12" height="12" rx="1.5" />
      <path d="M9 3v3M12 3v3M15 3v3M9 18v3M12 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3" />
    </svg>
  ),
  games: () => (
    <svg {...common}>
      <path d="M6 9h12a3 3 0 0 1 3 3v3a3 3 0 0 1-5.2 2L14 15h-4l-1.8 2A3 3 0 0 1 3 15v-3a3 3 0 0 1 3-3z" />
      <path d="M8 11v3M6.5 12.5h3M15.5 12h.01M17.5 13.5h.01" />
    </svg>
  ),
  cpu: () => (
    <svg {...common}>
      <rect x="7" y="7" width="10" height="10" rx="1" />
      <path d="M10 10h4v4h-4z" />
      <path d="M10 4v3M14 4v3M10 17v3M14 17v3M4 10h3M4 14h3M17 10h3M17 14h3" />
    </svg>
  ),
  capture: () => (
    <svg {...common}>
      <rect x="3" y="6" width="13" height="12" rx="2" />
      <path d="M16 10l5-3v10l-5-3" />
    </svg>
  ),
  audio: () => (
    <svg {...common}>
      <path d="M4 10v4M8 7v10M12 4v16M16 8v8M20 11v2" />
    </svg>
  ),
  health: () => (
    <svg {...common}>
      <path d="M12 3l7 3v5c0 4.5-3 8-7 10-4-2-7-5.5-7-10V6z" />
      <path d="M9 12l2 2 4-4" />
    </svg>
  ),
  settings: () => (
    <svg {...common}>
      <path d="M4 7h10M18 7h2M4 17h4M12 17h8" />
      <circle cx="16" cy="7" r="2" />
      <circle cx="10" cy="17" r="2" />
    </svg>
  ),
  developer: () => (
    <svg {...common}>
      <path d="M8 8l-4 4 4 4M16 8l4 4-4 4M13.5 5l-3 14" />
    </svg>
  ),
};
