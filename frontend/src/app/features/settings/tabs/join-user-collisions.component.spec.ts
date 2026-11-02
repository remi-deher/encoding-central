import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { JoinUserCollisionsComponent } from './join-user-collisions.component';

describe('Join account collision resolution', () => {
  it('shows the host identity and submits an explicit rename with the expected local name', async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(JoinUserCollisionsComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 10));
    const collision = {
      localUserId: 'local',
      expectedUsername: 'ADMIN',
      hostUsername: 'admin',
      proposedUsername: 'ADMIN-local',
    };
    http.expectOne('/api/v1/cluster/join-collisions').flush([collision]);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('admin');
    fixture.componentInstance['select'](collision);
    expect(fixture.componentInstance['form']()?.initialValues?.['username']).toBe('ADMIN-local');
    const pending = fixture.componentInstance['resolve']({ username: 'admin-50' });
    const request = http.expectOne('/api/v1/cluster/join-collisions/local/resolve');
    expect(request.request.body).toEqual({ expectedUsername: 'ADMIN', username: 'admin-50' });
    request.flush(null);
    await pending;
    expect(fixture.componentInstance['collisions']()).toEqual([]);
    fixture.destroy();
    http.verify();
  });
});
