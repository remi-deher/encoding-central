import { inject, Injector } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { firstValueFrom } from 'rxjs';
export interface UnsavedChanges {
  hasUnsavedChanges(): boolean;
}
export const unsavedChangesGuard: CanDeactivateFn<UnsavedChanges> = (component) => {
  if (!component.hasUnsavedChanges()) return true;
  const injector = inject(Injector);
  return Promise.all([
    import('@angular/material/dialog'),
    import('../ui/confirmation-dialog.component'),
  ]).then(async ([{ MatDialog }, { ConfirmationDialogComponent }]) => {
    const result = await firstValueFrom(
      injector
        .get(MatDialog)
        .open(ConfirmationDialogComponent, {
          data: {
            title: 'Quitter sans enregistrer ?',
            message: 'Les modifications seront perdues.',
            confirmLabel: 'Quitter sans enregistrer',
          },
        })
        .afterClosed(),
    );
    return result === true;
  });
};
