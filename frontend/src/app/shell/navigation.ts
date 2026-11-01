import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

export interface Destination {
  path: string;
  label: string;
  exact: boolean;
  icon: 'overview' | 'settings';
}

export const destinations: readonly Destination[] = [
  { path: '/', label: 'Vue d’ensemble', exact: true, icon: 'overview' },
  { path: '/parametres', label: 'Paramètres', exact: true, icon: 'settings' },
];

@Component({
  selector: 'app-primary-navigation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './navigation.html',
})
export class PrimaryNavigation {
  readonly collapsed = input(false);
  readonly mobileDock = input(false);
  protected readonly destinations = destinations;
}
