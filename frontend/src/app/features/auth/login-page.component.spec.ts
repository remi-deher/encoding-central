import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { NotificationService } from '../../shared/services/notification.service';
import { LoginPageComponent } from './login-page.component';

describe('LoginPageComponent', () => {
  function setup(login: (u: string, p: string) => Promise<void>) {
    const navigateByUrl = vi.fn().mockResolvedValue(true);
    const show = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { login } },
        { provide: Router, useValue: { navigateByUrl } },
        { provide: NotificationService, useValue: { show } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap({ returnUrl: '/nodes' }) } },
        },
      ],
    });
    const fixture = TestBed.createComponent(LoginPageComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const fill = (name: string, value: string) => {
      const input = el.querySelector(`[formcontrolname="${name}"]`) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    };
    const submit = async () => {
      el.querySelector('form')!.dispatchEvent(new Event('submit'));
      await fixture.whenStable();
    };
    return { fixture, el, fill, submit, navigateByUrl, show };
  }

  it('does not call the API while fields are empty', async () => {
    const login = vi.fn().mockResolvedValue(undefined);
    const { submit, navigateByUrl } = setup(login);
    await submit();
    expect(login).not.toHaveBeenCalled();
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('signs in with a trimmed username then goes to the return URL', async () => {
    const login = vi.fn().mockResolvedValue(undefined);
    const { fill, submit, navigateByUrl } = setup(login);
    fill('username', '  remi ');
    fill('password', 'secret');
    await submit();
    expect(login).toHaveBeenCalledWith('remi', 'secret');
    expect(navigateByUrl).toHaveBeenCalledWith('/nodes');
  });

  it('notifies and stays on the page when the login fails', async () => {
    const login = vi.fn().mockRejectedValue(new Error('refus'));
    const { fill, submit, navigateByUrl, show } = setup(login);
    fill('username', 'remi');
    fill('password', 'mauvais');
    await submit();
    expect(show).toHaveBeenCalled();
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('toggles password visibility', () => {
    const { fixture, el } = setup(vi.fn());
    const input = el.querySelector('[formcontrolname="password"]') as HTMLInputElement;
    expect(input.type).toBe('password');
    (el.querySelector('.auth-eye') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(input.type).toBe('text');
  });
});
