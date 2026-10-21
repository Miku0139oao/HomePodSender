/* 线性小图标（stroke 跟随 currentColor） */

export function CheckIcon({ size = 12, strokeWidth = 1.9 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 12 12" fill="none" aria-hidden="true">
      <path d="M2.6 6.3 4.9 8.5 9.4 3.6" stroke="currentColor" strokeWidth={strokeWidth} strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

export function PlusIcon({ size = 12 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 12 12" fill="none" aria-hidden="true">
      <path d="M6 2.2v7.6M2.2 6h7.6" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  );
}

export function GearIcon({ size = 14 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <path d="M12 15.2a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4Z" stroke="currentColor" strokeWidth="1.7" />
      <path
        d="M19.4 13.5a7.6 7.6 0 0 0 0-3l2-1.6-2-3.4-2.4.9a7.8 7.8 0 0 0-2.6-1.5L14 2.4h-4l-.4 2.5A7.8 7.8 0 0 0 7 6.4l-2.4-.9-2 3.4 2 1.6a7.6 7.6 0 0 0 0 3l-2 1.6 2 3.4 2.4-.9a7.8 7.8 0 0 0 2.6 1.5l.4 2.5h4l.4-2.5a7.8 7.8 0 0 0 2.6-1.5l2.4.9 2-3.4-2-1.6Z"
        stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"
      />
    </svg>
  );
}

export function BackIcon({ size = 14 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <path d="M14.5 5.5 8 12l6.5 6.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/** 实心喇叭（白色，参考图圆按钮里的白色文档图标风格） */
export function SpeakerIcon({ size = 16, muted = false }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <path d="M4.5 9.2h3.2L12.4 5c.6-.5 1.6-.1 1.6.7v12.6c0 .8-1 1.2-1.6.7l-4.7-4.2H4.5c-.6 0-1-.4-1-1V10.2c0-.6.4-1 1-1Z" fill="currentColor" />
      {muted ? (
        <path d="m17 9.5 4.5 5m0-5-4.5 5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
      ) : (
        <>
          <path d="M16.6 9.1a4.2 4.2 0 0 1 0 5.8" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
          <path d="M18.9 6.8a7.4 7.4 0 0 1 0 10.4" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
        </>
      )}
    </svg>
  );
}

export function PlayGlyph({ size = 18 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">
      <path d="M8 5.6v12.8c0 .8.9 1.3 1.6.9l10-6.4c.6-.4.6-1.3 0-1.7l-10-6.4c-.7-.5-1.6 0-1.6.8Z" fill="currentColor" />
    </svg>
  );
}

export function PauseGlyph({ size = 18 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">
      <rect x="6.2" y="5" width="4" height="14" rx="1.4" fill="currentColor" />
      <rect x="13.8" y="5" width="4" height="14" rx="1.4" fill="currentColor" />
    </svg>
  );
}
