import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { AppShellComponent } from './app-shell.component';
import { AuthService } from '../auth/auth.service';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';

describe('AppShellComponent', () => {
  beforeEach(() => {
    localStorage.removeItem('nav_collapsed');
    TestBed.configureTestingModule({
      imports: [AppShellComponent],
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            user: signal({ id: 'u-1', username: 'admin', role: 'admin' }),
            isAdmin: signal(true),
          },
        },
        {
          provide: ClusterOverview,
          useValue: {
            state$: of({
              ...INITIAL_CLUSTER_OVERVIEW,
              status: 'ready',
              snapshot: {
                identity: { clusterId: 'c-1', clusterName: 'Cluster Test', standalone: false },
                nodes: [
                  { id: 'n-1', name: 'srv-1', status: 'online' },
                  { id: 'n-2', name: 'srv-2', status: 'online' },
                ],
                libraries: [],
                flows: [],
              },
            }),
          },
        },
        {
          provide: ExecutionTracking,
          useValue: {
            state$: of({
              ...INITIAL_EXECUTION_TRACKING,
              status: 'ready',
              data: {
                activeCount: 3,
                queuedCount: 1,
                completedCount: 10,
                failedCount: 0,
                items: [],
              },
            }),
          },
        },
      ],
    });
  });

  it('renders the skip-link and application sidebar without a global search bar', () => {
    const fixture = TestBed.createComponent(AppShellComponent);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const skipLink = host.querySelector('a.skip-to-content-link') as HTMLAnchorElement;
    expect(skipLink).toBeTruthy();
    expect(skipLink.getAttribute('href')).toBe('#main-content');
    expect(skipLink.textContent).toContain('Aller au contenu principal');

    const sidebar = host.querySelector('.application-sidebar');
    expect(sidebar).toBeTruthy();

    const mainContent = host.querySelector('main#main-content');
    expect(mainContent).toBeTruthy();

    expect(host.querySelector('.topbar-search-pill')).toBeNull();
    expect(host.querySelector('.topbar-cluster-pill')).toBeNull();
  });

  it('toggles navigation rail collapse state and persists it to localStorage', () => {
    const fixture = TestBed.createComponent(AppShellComponent);
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    expect(comp['navigationCollapsed']()).toBe(false);

    comp['toggleNavigationCollapsed']();
    fixture.detectChanges();

    expect(comp['navigationCollapsed']()).toBe(true);
    expect(localStorage.getItem('nav_collapsed')).toBe('true');
  });
});
