import { ChangeDetectionStrategy, Component, effect, input, output, signal } from '@angular/core';
import { FormArray, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

/** Éditeur du document de profil de sous-titres (formatVersion 1). Les mesures sont celles du canevas de référence. */
@Component({
  selector: 'app-subtitle-profile-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
  ],
  template: `
    <form class="profile-form" [formGroup]="form" (ngSubmit)="submit()">
      @if (mode() === 'create') {
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Nom</mat-label>
          <input matInput formControlName="name" maxlength="128" [readonly]="busy()" />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Description</mat-label>
          <textarea matInput rows="2" formControlName="description" maxlength="1000" [readonly]="busy()"></textarea>
        </mat-form-field>
      }
      <div class="grid" formGroupName="script">
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Version du profil</mat-label>
          <input matInput formControlName="profileVersion" maxlength="16" [readonly]="busy()" />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Titre du script</mat-label>
          <input matInput formControlName="title" maxlength="128" [readonly]="busy()" />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Largeur de référence</mat-label>
          <input matInput type="number" formControlName="width" [readonly]="busy()" />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Hauteur de référence</mat-label>
          <input matInput type="number" formControlName="height" [readonly]="busy()" />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>WrapStyle</mat-label>
          <mat-select formControlName="wrapStyle">
            @for (option of wrapStyles; track option.value) {
              <mat-option [value]="option.value">{{ option.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Matrice YCbCr</mat-label>
          <mat-select formControlName="matrix">
            @for (matrix of matrices; track matrix) {
              <mat-option [value]="matrix">{{ matrix }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>
      <mat-checkbox formControlName="scaledBorderAndShadow">Bordure et ombre à l’échelle</mat-checkbox>
      <div formArrayName="styles" class="styles">
        @for (style of styles.controls; track $index) {
          <mat-expansion-panel [formGroupName]="$index">
            <mat-expansion-panel-header>
              <mat-panel-title>{{ style.controls.name.value || 'Style ' + ($index + 1) }}</mat-panel-title>
            </mat-expansion-panel-header>
            <div class="grid">
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Nom</mat-label>
                <input matInput formControlName="name" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Police</mat-label>
                <input matInput formControlName="fontName" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Taille</mat-label>
                <input matInput type="number" formControlName="fontSize" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Couleur principale</mat-label>
                <input matInput formControlName="primaryColour" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Couleur secondaire</mat-label>
                <input matInput formControlName="secondaryColour" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Couleur du contour</mat-label>
                <input matInput formControlName="outlineColour" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Couleur de fond</mat-label>
                <input matInput formControlName="backColour" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Graisse</mat-label>
                <mat-select formControlName="bold">
                  @for (flag of flags; track flag.value) {
                    <mat-option [value]="flag.value">{{ flag.label }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Italique</mat-label>
                <mat-select formControlName="italic">
                  @for (flag of flags; track flag.value) {
                    <mat-option [value]="flag.value">{{ flag.label }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Souligné</mat-label>
                <mat-select formControlName="underline">
                  @for (flag of flags; track flag.value) {
                    <mat-option [value]="flag.value">{{ flag.label }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Barré</mat-label>
                <mat-select formControlName="strikeOut">
                  @for (flag of flags; track flag.value) {
                    <mat-option [value]="flag.value">{{ flag.label }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Échelle X</mat-label>
                <input matInput type="number" formControlName="scaleX" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Échelle Y</mat-label>
                <input matInput type="number" formControlName="scaleY" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Espacement</mat-label>
                <input matInput type="number" formControlName="spacing" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Angle</mat-label>
                <input matInput type="number" formControlName="angle" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Style de bordure</mat-label>
                <mat-select formControlName="borderStyle">
                  <mat-option [value]="1">Contour</mat-option>
                  <mat-option [value]="3">Boîte opaque</mat-option>
                </mat-select>
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Contour</mat-label>
                <input matInput type="number" formControlName="outline" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Ombre</mat-label>
                <input matInput type="number" formControlName="shadow" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Alignement</mat-label>
                <input matInput type="number" formControlName="alignment" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Marge gauche</mat-label>
                <input matInput type="number" formControlName="marginL" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Marge droite</mat-label>
                <input matInput type="number" formControlName="marginR" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Marge verticale</mat-label>
                <input matInput type="number" formControlName="marginV" [readonly]="busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Encodage</mat-label>
                <input matInput type="number" formControlName="encoding" [readonly]="busy()" />
              </mat-form-field>
            </div>
            <button matButton="text" type="button" [disabled]="busy() || styles.length < 2" (click)="removeStyle($index)">
              Retirer ce style
            </button>
          </mat-expansion-panel>
        }
      </div>
      <button matButton="outlined" type="button" [disabled]="busy() || styles.length >= 32" (click)="addStyle()">
        Ajouter un style
      </button>
      <p class="hint">
        Tailles, contour, ombre et marges sont ceux du canevas de référence. Le moteur les met à l’échelle du cadre
        affiché : la hauteur pour le corps, le contour, l’ombre et la marge verticale ; la largeur pour les marges
        horizontales.
      </p>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <div class="actions">
        <button matButton="text" type="button" [disabled]="busy()" (click)="cancelled.emit()">Annuler</button>
        <button matButton="filled" type="submit" [disabled]="busy()">
          {{ mode() === 'version' ? 'Publier la version' : 'Ajouter' }}
        </button>
      </div>
    </form>
  `,
  styles: `
    .profile-form,
    .styles {
      display: grid;
      gap: var(--space-3);
    }
    .grid {
      display: grid;
      gap: var(--space-3);
      grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
    }
    .hint {
      color: var(--text-secondary);
      font-size: var(--font-body);
      margin: 0;
    }
    .error {
      color: var(--danger);
      margin: 0;
    }
    .actions {
      display: flex;
      gap: var(--space-2);
      justify-content: flex-end;
    }
  `,
})
export class SubtitleProfileEditorComponent {
  readonly busy = input(false);
  readonly mode = input<'create' | 'version'>('create');
  readonly documentText = input<string | null>(null);
  readonly submitted = output<{ name: string; description: string; file: File }>();
  readonly cancelled = output<void>();
  protected readonly error = signal('');
  protected readonly flags = [
    { value: -1, label: 'Oui' },
    { value: 0, label: 'Non' },
    { value: 1, label: 'Hérité' },
  ];
  protected readonly wrapStyles = [
    { value: 0, label: '0 · fin de ligne intelligente' },
    { value: 1, label: '1 · fin de ligne, le \\N termine' },
    { value: 2, label: '2 · pas de retour automatique' },
    { value: 3, label: '3 · fin de ligne, le \\N aussi' },
  ];
  protected readonly matrices = ['TV.601', 'PC.601', 'TV.709', 'PC.709', 'TV.240M', 'TV.2020'];
  protected readonly form = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(128)] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(1000)] }),
    scaledBorderAndShadow: new FormControl(true, { nonNullable: true }),
    script: new FormGroup({
      profileVersion: new FormControl('1', { nonNullable: true, validators: [Validators.required, Validators.maxLength(16)] }),
      title: new FormControl('Sous-titres', { nonNullable: true, validators: [Validators.required, Validators.maxLength(128)] }),
      width: new FormControl(640, { nonNullable: true, validators: [Validators.required, Validators.min(16), Validators.max(8192)] }),
      height: new FormControl(360, { nonNullable: true, validators: [Validators.required, Validators.min(16), Validators.max(8192)] }),
      wrapStyle: new FormControl(0, { nonNullable: true }),
      matrix: new FormControl('TV.601', { nonNullable: true }),
    }),
    styles: new FormArray([styleGroup()]),
  });

  constructor() {
    effect(() => {
      const text = this.documentText();
      if (!text) return;
      this.applyDocument(text);
    });
  }

  protected get styles(): FormArray<ReturnType<typeof styleGroup>> {
    return this.form.controls.styles;
  }

  protected addStyle(): void {
    this.styles.push(styleGroup({ name: `Style${this.styles.length + 1}` }));
  }

  protected removeStyle(index: number): void {
    if (this.styles.length < 2) return;
    this.styles.removeAt(index);
  }

  protected submit(): void {
    this.error.set('');
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set('Complétez les champs du profil.');
      return;
    }
    const value = this.form.getRawValue();
    const colour = /^&H[0-9A-Fa-f]{8}$/;
    for (const style of value.styles) {
      if (![style.primaryColour, style.secondaryColour, style.outlineColour, style.backColour].every((item) => colour.test(item))) {
        this.error.set(`Couleur invalide pour « ${style.name} » : attendu &H puis 8 chiffres hexadécimaux.`);
        return;
      }
    }
    const document = {
      formatVersion: 1,
      profileVersion: value.script.profileVersion.trim(),
      reference: { width: Number(value.script.width), height: Number(value.script.height) },
      script: {
        title: value.script.title.trim(),
        wrapStyle: Number(value.script.wrapStyle),
        scaledBorderAndShadow: value.scaledBorderAndShadow,
        ycbcrMatrix: value.script.matrix,
      },
      styles: value.styles.map((style) => ({
        ...style,
        name: style.name.trim(),
        fontName: style.fontName.trim(),
      })),
    };
    const fileName = `${value.name.trim().replace(/[^\w.-]+/g, '-').replace(/^-|-$/g, '') || 'profil'}.json`;
    const file = new File([JSON.stringify(document, null, 2)], fileName, { type: 'application/json' });
    this.submitted.emit({
      name: value.name.trim(),
      description: value.description.trim(),
      file,
    });
  }

  private applyDocument(text: string): void {
    try {
      const document = JSON.parse(text) as {
        profileVersion?: string;
        reference?: { width?: number; height?: number };
        script?: { title?: string; wrapStyle?: number; scaledBorderAndShadow?: boolean; ycbcrMatrix?: string };
        styles?: ReadonlyArray<Record<string, string | number | boolean>>;
      };
      this.form.patchValue({
        scaledBorderAndShadow: document.script?.scaledBorderAndShadow !== false,
        script: {
          profileVersion: document.profileVersion ?? '1',
          title: document.script?.title ?? 'Sous-titres',
          width: document.reference?.width ?? 640,
          height: document.reference?.height ?? 360,
          wrapStyle: document.script?.wrapStyle ?? 0,
          matrix: document.script?.ycbcrMatrix ?? 'TV.601',
        },
      });
      this.styles.clear();
      for (const style of document.styles ?? []) this.styles.push(styleGroup(style));
      if (this.styles.length === 0) this.styles.push(styleGroup());
      this.error.set('');
    } catch {
      this.error.set('Le fichier de cette version n’a pas pu être relu. Saisissez le profil à nouveau.');
    }
  }
}

function styleGroup(value: Record<string, string | number | boolean> = {}) {
  const text = (key: string, fallback: string) =>
    new FormControl(typeof value[key] === 'string' ? String(value[key]) : fallback, { nonNullable: true, validators: [Validators.required] });
  const number = (key: string, fallback: number) =>
    new FormControl(typeof value[key] === 'number' ? Number(value[key]) : fallback, { nonNullable: true, validators: [Validators.required] });
  return new FormGroup({
    name: text('name', 'Default'),
    fontName: text('fontName', 'Arial'),
    fontSize: number('fontSize', 23),
    primaryColour: text('primaryColour', '&H00FFFFFF'),
    secondaryColour: text('secondaryColour', '&H000000FF'),
    outlineColour: text('outlineColour', '&H00000000'),
    backColour: text('backColour', '&H00000000'),
    bold: number('bold', 0),
    italic: number('italic', 0),
    underline: number('underline', 0),
    strikeOut: number('strikeOut', 0),
    scaleX: number('scaleX', 100),
    scaleY: number('scaleY', 100),
    spacing: number('spacing', 0),
    angle: number('angle', 0),
    borderStyle: number('borderStyle', 1),
    outline: number('outline', 2),
    shadow: number('shadow', 0),
    alignment: number('alignment', 2),
    marginL: number('marginL', 20),
    marginR: number('marginR', 20),
    marginV: number('marginV', 20),
    encoding: number('encoding', 1),
  });
}
