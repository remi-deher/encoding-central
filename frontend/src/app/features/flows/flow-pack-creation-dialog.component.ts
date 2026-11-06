import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogContent, MatDialogRef } from '@angular/material/dialog';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import { DialogFrameComponent } from '../../shared/ui/dialog-frame.component';

export interface FlowPackCreationDialogData {
  readonly form: RecordFormData;
  readonly create: (values: Record<string, string>) => Promise<boolean>;
}

@Component({
  selector: 'app-flow-pack-creation-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogFrameComponent, MatDialogContent, RecordFormComponent],
  template: `
    <app-dialog-frame eyebrow="Packs" heading="Créer un pack">
      <mat-dialog-content class="app-dialog-body">
        <app-record-form
          [data]="data.form"
          (submitted)="submit($event)"
          (cancelled)="dialogRef.close(false)"
        />
      </mat-dialog-content>
    </app-dialog-frame>
  `,
})
export class FlowPackCreationDialogComponent {
  protected readonly data = inject<FlowPackCreationDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<FlowPackCreationDialogComponent>);

  protected async submit(values: Record<string, string>): Promise<void> {
    if (await this.data.create(values)) this.dialogRef.close(true);
  }
}
