import { ChangeDetectionStrategy, Component, input } from '@angular/core';

const ICONS = {
  condition: 'm12 3 9 9-9 9-9-9ZM12 8v5m0 3h.01',
  warning: 'm12 3 10 18H2ZM12 9v4m0 4h.01',
  clock: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM12 7v5l3 2',
  skip: 'M4 5v6h6M4 11a8 8 0 1 1 3 7',
  upload: 'M12 16V3m-5 5 5-5 5 5M4 15v6h16v-6',
  copy: 'M9 9h12v12H9ZM15 5V3H3v12h2',
  refresh: 'M20 7v5h-5M4 17v-5h5M6 7a7 7 0 0 1 12-2l2 3M4 16l2 3a7 7 0 0 0 12-2',
  server: 'M3 3h18v7H3Zm0 11h18v7H3ZM7 6h.01M7 17h.01',
  terminal: 'm5 7 4 5-4 5M12 17h7',
  api: 'm8 5-6 7 6 7m8-14 6 7-6 7M14 3l-4 18',
  back: 'M19 12H5m7-7-7 7 7 7',
  forward: 'M5 12h14m-7-7 7 7-7 7',
  outward: 'M7 17 17 7M7 7h10v10',
  undo: 'M9 5 4 10l5 5M4 10h10a6 6 0 0 1 0 12',
  redo: 'm15 5 5 5-5 5m5-5H10a6 6 0 0 0 0 12',
  up: 'm5 15 7-7 7 7',
  down: 'm5 9 7 7 7-7',
  left: 'm15 5-7 7 7 7',
  right: 'm9 5 7 7-7 7',
  more: 'M5 12h.01M12 12h.01M19 12h.01',
  add: 'M12 5v14M5 12h14',
  attach:
    'm20 11-8.5 8.5a5 5 0 0 1-7-7L13 4a3.3 3.3 0 0 1 4.7 4.7l-8.6 8.6a1.7 1.7 0 0 1-2.4-2.4L14.5 7',
  flow: 'M3 3h6v5H3ZM15 3h6v5h-6ZM9 20h6v-5H9ZM6 8v3h12V8M12 11v4',
  history: 'M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l3 2',
  library: 'M3 6a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z',
  close: 'm6 6 12 12M6 18 18 6',
  play: 'm8 5 11 7-11 7Z',
  check: 'm5 12 4 4L19 6',
  save: 'M5 3h12l4 4v14H3V3Zm2 0v7h10V3M7 21v-7h10v7',
  settings: 'M4 7h16M4 17h16M8 4v6m8 4v6',
  pack: 'm3 7 9-4 9 4v10l-9 4-9-4Zm0 0 9 4 9-4M12 11v10',
  link: 'm10 13 4-4M8 16l-2 2a4 4 0 0 1-6-6l4-4a4 4 0 0 1 6 0m4 0 2-2a4 4 0 0 1 6 6l-4 4a4 4 0 0 1-6 0',
  pin: 'M9 3h6l-1 6 4 4H6l4-4Zm3 10v8',
  edit: 'm4 16 12-12 4 4L8 20H4Zm10-10 4 4',
  sort: 'M8 4v16m-4-4 4 4 4-4M16 20V4m-4 4 4-4 4 4',
  eye: 'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Zm10 3a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z',
  'eye-off':
    'M3 3l18 18M10.6 5.1A9.7 9.7 0 0 1 12 5c6 0 10 7 10 7a17 17 0 0 1-3.2 3.9M6.6 6.6C3.7 8.4 2 12 2 12s4 7 10 7a9.6 9.6 0 0 0 4.4-1M9.9 9.9a3 3 0 0 0 4.2 4.2',
  video:
    'M5 5h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2Zm5 4.5 5 2.5-5 2.5Z',
  image:
    'M5 4h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Zm4 4.5h.01M21 16l-5-5L7 20',
  music:
    'M9 18V6l11-2v12M6.5 15.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5Zm11-2a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5Z',
  subtitles:
    'M5 5h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2Zm2 6h4m2 0h4M7 15h2m3 0h5',
  sell: 'M20 11.5 12.5 4H4v8.5L11.5 20 20 11.5ZM8 8h.01',
  download: 'M12 3v13m-5-5 5 5 5-5M4 19v2h16v-2',
  info: 'M12 8h.01M12 12v5M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Z',
  volume: 'M11 5L6 9H2v6h4l5 4V5Zm4.5 3.5a5 5 0 0 1 0 7M19 6a9 9 0 0 1 0 12',
  document: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6Zm0 0v6h6',
  folder: 'M3 7v13a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-7l-2-2H5a2 2 0 0 0-2 2Z',
} as const;
export type IconName = keyof typeof ICONS;
@Component({
  selector: 'app-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true' },
  template:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" focusable="false"><path [attr.d]="paths[name()]" /></svg>',
  styles:
    ':host { display: inline-flex; width: 20px; height: 20px; flex-shrink: 0; vertical-align: middle; } svg { width: 100%; height: 100%; }',
})
export class IconComponent {
  readonly name = input.required<IconName>();
  protected readonly paths = ICONS;
}
