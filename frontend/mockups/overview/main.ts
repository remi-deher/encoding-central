import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ThemeToggleComponent } from '../../src/app/core/theme/theme-toggle.component';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatButtonToggleModule, MatProgressBarModule, ThemeToggleComponent],
  templateUrl: './mockup.html',
  styleUrl: './mockup.scss',
})
class OverviewMockup {
  readonly period = signal('24 h');
  readonly selected = signal<string | null>(null);
  readonly jobs = [
    {
      name: 'Dune : Deuxième partie',
      detail: 'Films · HEVC 10-bit · 2160p',
      node: 'Studio',
      progress: 72,
      step: 'Encodage vidéo',
      time: '12 min restantes',
    },
    {
      name: 'Shōgun · S01E04',
      detail: 'Séries · HEVC 10-bit · 1080p',
      node: 'Serveur NAS',
      progress: 46,
      step: 'Encodage vidéo',
      time: '8 min restantes',
    },
    {
      name: 'Frieren · S01E12',
      detail: 'Animation · Audio AAC / AC3',
      node: 'Mini PC',
      progress: 91,
      step: 'Validation de la sortie',
      time: '1 min restante',
    },
  ];
  readonly nodes = [
    { name: 'Studio', detail: 'RTX 4070 · 2 / 3 slots', cpu: 38, memory: 54 },
    { name: 'Serveur NAS', detail: 'Intel Quick Sync · 1 / 2 slots', cpu: 62, memory: 41 },
    { name: 'Mini PC', detail: 'CPU · 1 / 1 slot', cpu: 84, memory: 68 },
  ];
}
bootstrapApplication(OverviewMockup).catch(console.error);
