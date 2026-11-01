import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Router } from '@angular/router';
import { apiErrorMessage } from '../core/api-error';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-login-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  templateUrl: './login-page.html',
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly pending = signal(false);
  protected readonly error = signal('');
  protected readonly form = inject(FormBuilder).nonNullable.group({
    username: ['', Validators.required],
    password: ['', Validators.required],
  });

  protected async submit(): Promise<void> {
    if (this.pending()) return;
    this.error.set('');
    this.pending.set(true);
    const { username, password } = this.form.getRawValue();
    try {
      await this.auth.login(username, password);
      await this.router.navigateByUrl('/');
    } catch (error) {
      this.error.set(apiErrorMessage(error, 'La connexion a échoué.'));
    } finally {
      this.pending.set(false);
    }
  }
}
