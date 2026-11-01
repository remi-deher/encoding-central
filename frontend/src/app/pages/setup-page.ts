import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Router } from '@angular/router';
import { apiErrorMessage } from '../core/api-error';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-setup-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  templateUrl: './setup-page.html',
})
export class SetupPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly pending = signal(false);
  protected readonly error = signal('');
  protected readonly form = inject(FormBuilder).nonNullable.group({
    displayName: ['Encoding', Validators.required],
    publicBaseUrl: [defaultPublicUrl(), Validators.required],
    username: ['', Validators.required],
    password: ['', Validators.required],
  });

  protected async submit(): Promise<void> {
    if (this.pending()) return;
    this.error.set('');
    this.pending.set(true);
    const value = this.form.getRawValue();
    try {
      await this.auth.setup(value);
      await this.router.navigateByUrl('/');
    } catch (error) {
      this.error.set(apiErrorMessage(error, 'La mise en service a échoué.'));
    } finally {
      this.pending.set(false);
    }
  }
}

function defaultPublicUrl(): string {
  return `${window.location.protocol}//${window.location.host}`;
}
