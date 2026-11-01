import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';

const storedTheme = localStorage.getItem('encoding-theme');
const storedPalette = localStorage.getItem('encoding-palette');
const theme =
  storedTheme === 'light' || storedTheme === 'dark'
    ? storedTheme
    : window.matchMedia('(prefers-color-scheme: dark)').matches
      ? 'dark'
      : 'light';
document.documentElement.dataset['theme'] = theme;
if (storedPalette === 'ocean' || storedPalette === 'iris' || storedPalette === 'rose') {
  document.documentElement.dataset['palette'] = storedPalette;
}

bootstrapApplication(App, appConfig).catch((err) => console.error(err));
