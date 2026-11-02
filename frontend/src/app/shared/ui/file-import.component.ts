import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { IconComponent } from './icon.component';
import { MatButtonModule } from '@angular/material/button';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-file-import',
  imports: [MatButtonModule, IconComponent],
  template: `
    <input
      #picker
      hidden
      type="file"
      [accept]="accept()"
      [disabled]="busy()"
      (change)="select(picker)"
    />
    <button matButton="outlined" type="button" [disabled]="busy()" (click)="picker.click()">
      <app-icon aria-hidden="true" name="upload" />
      {{ busy() ? 'Lecture en cours…' : label() }}
    </button>
    @if (description()) {
      <p>{{ description() }}</p>
    }
  `,
  styles: `
    app-icon {
      margin-inline-end: var(--space-2);
    }
    p {
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.5;
    }
  `,
})
export class FileImportComponent {
  readonly accept = input('');
  readonly busy = input(false);
  readonly label = input('Importer un fichier');
  readonly description = input('');
  readonly selected = output<File>();
  protected select(input: HTMLInputElement): void {
    const file = input.files?.[0];
    if (file) this.selected.emit(file);
    input.value = '';
  }
}
