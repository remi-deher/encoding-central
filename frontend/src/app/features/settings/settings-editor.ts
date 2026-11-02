import { DestroyRef, Injectable, TemplateRef, inject, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';

interface SettingsEditorState {
  readonly title: string;
  readonly content: TemplateRef<unknown>;
  readonly dirty?: () => boolean;
  readonly busy?: () => boolean;
  readonly onClosed?: () => void;
}
/** One editor and one drawer animation at a time; closed callbacks cannot clear a newer editor. */
@Injectable({ providedIn: 'root' })
export class SettingsEditor {
  private readonly dialog = inject(MatDialog);
  readonly state = signal<SettingsEditorState | null>(null);
  private closing: Promise<void> | null = null;
  private acknowledgeClose: (() => void) | null = null;
  private disposed = false;
  private request = 0;
  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.disposed = true;
      this.clearState();
      this.finishClose();
    });
  }
  hasUnsavedChanges(): boolean {
    return this.state()?.dirty?.() ?? false;
  }
  isBusy(): boolean {
    return this.state()?.busy?.() ?? false;
  }
  async open(state: SettingsEditorState): Promise<void> {
    const request = ++this.request;
    if (!(await this.close())) return;
    if (this.closing) await this.closing;
    if (!this.disposed && request === this.request) this.state.set(state);
  }
  async close(): Promise<boolean> {
    if (this.isBusy()) return false;
    if (this.hasUnsavedChanges() && !(await this.confirmDiscard())) return false;
    this.reset();
    return true;
  }
  reset(): void {
    if (this.state() && !this.disposed && !this.closing)
      this.closing = new Promise((resolve) => (this.acknowledgeClose = resolve));
    this.clearState();
  }
  /** Called only when the shared drawer has finished closing. */
  drawerClosed(): void {
    this.clearState();
    this.finishClose();
  }
  private clearState(): void {
    const previous = this.state();
    this.state.set(null);
    previous?.onClosed?.();
  }
  private finishClose(): void {
    this.acknowledgeClose?.();
    this.acknowledgeClose = null;
    this.closing = null;
  }
  async confirmDiscard(): Promise<boolean> {
    return (
      (await firstValueFrom(
        this.dialog
          .open(ConfirmationDialogComponent, {
            data: {
              title: 'Quitter sans enregistrer ?',
              message: 'Les modifications non enregistrées seront perdues.',
              confirmLabel: 'Quitter sans enregistrer',
            },
          })
          .afterClosed(),
      )) === true
    );
  }
}
