import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { apiErrorMessage } from '../core/api-error';
import { ServerService } from '../core/server.service';

@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  templateUrl: './settings-page.html',
})
export class SettingsPage {
  private readonly servers = inject(ServerService);
  protected readonly pending = signal(false);
  protected readonly error = signal('');
  protected readonly saved = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    displayName: ['', Validators.required],
    publicBaseUrl: ['', Validators.required],
  });

  constructor() {
    void this.load();
  }

  protected async submit(): Promise<void> {
    if (this.pending()) return;
    this.error.set('');
    this.saved.set(false);
    this.pending.set(true);
    try {
      const updated = await this.servers.update(this.form.getRawValue());
      this.form.setValue({ displayName: updated.displayName, publicBaseUrl: updated.publicBaseUrl });
      this.saved.set(true);
    } catch (error) {
      this.error.set(apiErrorMessage(error, 'L’enregistrement a échoué.'));
    } finally {
      this.pending.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      const server = await this.servers.get();
      this.form.setValue({ displayName: server.displayName, publicBaseUrl: server.publicBaseUrl });
    } catch {
      this.error.set('Le serveur n’a pas répondu.');
    }
  }
}
