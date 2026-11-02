import { IconComponent } from '../../../shared/ui/icon.component';
import { MatMenuModule } from '@angular/material/menu';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MediaType } from '../../../shared/models/media-type';

@Component({
  selector: 'app-flow-command-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatButtonModule, MatTooltipModule, MatMenuModule, IconComponent],
  template: `
    <header class="command-bar" role="toolbar" aria-label="Commandes du flow">
      <div class="command-group navigation-group">
        <a
          mat-icon-button
          routerLink="/flows"
          class="back-link"
          aria-label="Retourner au catalogue des flows"
          matTooltip="Retourner au catalogue des flows"
        >
          <app-icon name="back" />
        </a>

        <div class="title-container">
          @if (isEditingTitle()) {
            <input
              #titleInput
              type="text"
              class="title-input"
              [value]="title()"
              (blur)="commitTitle(titleInput.value)"
              (keydown.enter)="commitTitle(titleInput.value)"
              (keydown.escape)="cancelEditingTitle()"
              aria-label="Nom du flow"
            />
          } @else {
            <h1
              class="flow-title"
              (click)="startEditingTitle()"
              tabindex="0"
              (keydown.enter)="startEditingTitle()"
              matTooltip="Cliquer pour renommer le flow"
              role="button"
              aria-label="Renommer le flow"
            >
              {{ title() }}
              <app-icon class="edit-hint" name="edit" />
            </h1>
          }

          <div class="status-pills">
            <span class="media-type-pill" [attr.data-media]="mediaType()">
              {{ mediaTypeLabel() }}
            </span>

            @if (hasUnsavedChanges()) {
              <span class="state-pill state-pill_unsaved" role="status">
                <span class="status-dot status-dot_amber" aria-hidden="true"></span>
                Non enregistré
              </span>
            } @else if (storedVersion() > 0) {
              <span class="state-pill state-pill_published" role="status">
                <span class="status-dot status-dot_green" aria-hidden="true"></span>
                v{{ storedVersion() }} publiée
              </span>
            } @else if (hasDraft()) {
              <span class="state-pill state-pill_draft" role="status">
                <span class="status-dot status-dot_blue" aria-hidden="true"></span>
                Brouillon
              </span>
            }

            @if (storedVersion() > 0 && backupStatus()) {
              <span
                class="backup-pill"
                [class.backup-pill_ready]="backupStatus() === 'ready'"
                [class.backup-pill_failed]="backupStatus() === 'failed'"
                matTooltip="État de la sauvegarde JSON persistante"
              >
                Sauvegarde :
                {{
                  backupStatus() === 'ready'
                    ? 'disponible'
                    : backupStatus() === 'failed'
                      ? 'en échec'
                      : 'en attente'
                }}
              </span>
            }
          </div>
        </div>
      </div>

      <div class="command-group workbench-actions">
        <button
          mat-icon-button
          type="button"
          [disabled]="!canUndo()"
          (click)="undoRequested.emit()"
          aria-label="Annuler la dernière modification"
          matTooltip="Annuler (Ctrl+Z)"
        >
          <app-icon name="undo" />
        </button>
        <button
          mat-icon-button
          type="button"
          [disabled]="!canRedo()"
          (click)="redoRequested.emit()"
          aria-label="Rétablir la modification"
          matTooltip="Rétablir (Ctrl+Maj+Z)"
        >
          <app-icon name="redo" />
        </button>
        <span class="mobile-toolbar-break" aria-hidden="true"></span>
        <button
          matButton="tonal"
          type="button"
          class="add-action"
          (click)="togglePalette.emit()"
          [attr.aria-pressed]="paletteOpen()"
          aria-label="Basculer la palette de blocs"
          matTooltip="Afficher la fenêtre des composants (Ctrl+B)"
        >
          <app-icon name="add" /> Composants
        </button>
        <button
          matButton="tonal"
          type="button"
          [disabled]="disabled() || simulatorBusy()"
          (click)="simulateRequested.emit()"
          matTooltip="Tester le parcours (Ctrl+Entrée)"
        >
          <app-icon name="play" /> Tester
        </button>
        <button
          matButton="filled"
          type="button"
          class="save-btn"
          [disabled]="saving() || disabled() || !canSave()"
          (click)="saveRequested.emit()"
          matTooltip="Enregistrer (Ctrl+S)"
        >
          <app-icon name="save" /> {{ saving() ? 'Enregistrement…' : 'Enregistrer' }}
        </button>
        <button
          mat-icon-button
          type="button"
          [matMenuTriggerFor]="moreMenu"
          aria-label="Autres actions du flow"
          matTooltip="Autres actions"
        >
          <app-icon name="more" />
        </button>
        <mat-menu #moreMenu="matMenu">
          <button mat-menu-item (click)="autoLayoutRequested.emit()">
            <app-icon name="sort" /> Disposition automatique
          </button>
          <button
            mat-menu-item
            [disabled]="disabled() || validating()"
            (click)="validateRequested.emit()"
          >
            <app-icon name="check" /> {{ validating() ? 'Vérification…' : 'Vérifier le flow' }}
          </button>
          @if (canCreatePack()) {
            <button
              mat-menu-item
              [disabled]="disabled() || saving()"
              (click)="packCreationRequested.emit()"
            >
              <app-icon name="pack" /> Créer un pack
            </button>
          }
          <button mat-menu-item (click)="simulationSettingsRequested.emit()">
            <app-icon name="settings" /> Profil de test
          </button>
          <button
            mat-menu-item
            (click)="toggleTray.emit()"
            aria-label="Basculer le volet de simulation"
          >
            <app-icon name="play" /> Résultats du test
          </button>
          <button mat-menu-item (click)="toggleConnections.emit()">
            <app-icon name="link" />
            {{ showConnections() ? 'Masquer les liaisons' : 'Afficher les liaisons' }}
          </button>
          @if (canRestoreAiProposal()) {
            <button mat-menu-item (click)="restoreAiProposalRequested.emit()">
              <app-icon name="undo" /> Annuler la proposition IA
            </button>
          }
        </mat-menu>
      </div>
    </header>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }
    .command-bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 16px;
      min-height: 56px;
      padding: 8px 16px;
      background: var(--surface-container);
    }
    .command-group {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .navigation-group {
      flex: 1 1 320px;
    }
    .back-link {
      color: var(--text-primary);
      flex-shrink: 0;
    }
    .title-container {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 8px;
      min-width: 0;
    }
    .flow-title {
      margin: 0;
      padding: 4px;
      font-size: var(--font-title-lg);
      font-weight: 700;
      display: flex;
      align-items: center;
      gap: 8px;
      cursor: pointer;
      border-radius: var(--radius-sm);
      overflow-wrap: anywhere;
    }
    .flow-title:focus-visible {
      outline: 2px solid var(--accent);
    }
    .edit-hint {
      opacity: 0.6;
      width: 16px;
      height: 16px;
    }
    .title-input {
      min-width: 0;
      max-width: 100%;
      font: inherit;
      font-weight: 700;
      background: var(--surface-container-highest);
      color: var(--text-primary);
      padding: 8px;
      border: 1px solid var(--accent);
      border-radius: var(--radius-md);
    }
    .status-pills {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .media-type-pill,
    .state-pill,
    .backup-pill {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 4px 10px;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      background: var(--surface-container-high);
      color: var(--text-secondary);
    }
    .state-pill_unsaved {
      background: var(--warning-soft);
      color: var(--text-primary);
    }
    .state-pill_published {
      background: var(--success-soft);
      color: var(--text-primary);
    }
    .state-pill_draft {
      background: var(--accent-soft);
      color: var(--text-primary);
    }
    .status-dot {
      width: 6px;
      height: 6px;
      border-radius: 50%;
      background: currentColor;
    }
    .status-dot_green {
      background: var(--success);
    }
    .status-dot_amber {
      background: var(--warning);
    }
    .status-dot_blue {
      background: var(--accent);
    }
    .backup-pill_ready {
      color: var(--success);
    }
    .backup-pill_failed {
      color: var(--danger);
    }
    .workbench-actions {
      flex-wrap: wrap;
    }
    .workbench-actions > button {
      flex-shrink: 0;
    }
    .workbench-actions button:not(.mat-mdc-icon-button) app-icon {
      margin-inline-end: 6px;
    }
    .mobile-toolbar-break {
      display: none;
    }
    @media (max-width: 768px) {
      .command-bar {
        padding: 6px 8px;
        gap: 4px;
      }
      .navigation-group {
        flex-basis: 100%;
      }
      .flow-title {
        font-size: var(--font-title-sm);
      }
      .workbench-actions {
        gap: 4px;
        flex-wrap: wrap;
        width: 100%;
        overflow: visible;
        padding-block: 4px;
      }
      .workbench-actions > * {
        flex-shrink: 0;
      }
      .mobile-toolbar-break {
        display: block;
        flex-basis: 100%;
        height: 0;
      }
      .save-btn app-icon {
        display: none;
      }
      .add-suffix {
        display: none;
      }
      .workbench-actions > button:not(.mat-mdc-icon-button) {
        padding-inline: 10px;
      }
      .backup-pill {
        display: none;
      }
    }
  `,
})
export class FlowCommandBarComponent {
  readonly canUndo = input(false);
  readonly canRedo = input(false);
  readonly undoRequested = output<void>();
  readonly redoRequested = output<void>();

  readonly title = input.required<string>();
  readonly mediaType = input.required<MediaType>();
  readonly storedVersion = input(0);
  readonly hasDraft = input(false);
  readonly hasUnsavedChanges = input(false);
  readonly backupStatus = input<string | null>(null);
  readonly editorStatus = input('');
  readonly nodeCount = input(0);
  readonly saving = input(false);
  readonly validating = input(false);
  readonly canSave = input(false);
  readonly disabled = input(false);
  readonly simulatorBusy = input(false);
  readonly canCreatePack = input(false);

  readonly paletteOpen = input(true);
  readonly trayOpen = input(false);
  readonly showConnections = input(true);

  readonly titleChange = output<string>();
  readonly saveRequested = output<void>();
  readonly validateRequested = output<void>();
  readonly simulateRequested = output<void>();
  readonly simulationSettingsRequested = output<void>();
  readonly packCreationRequested = output<void>();
  readonly restoreAiProposalRequested = output<void>();
  readonly canRestoreAiProposal = input(false);
  readonly togglePalette = output<void>();
  readonly toggleTray = output<void>();
  readonly toggleConnections = output<void>();
  readonly autoLayoutRequested = output<void>();

  protected readonly isEditingTitle = signal(false);

  protected readonly mediaTypeLabel = computed(() => {
    switch (this.mediaType()) {
      case 'video':
        return 'Vidéo';
      case 'music':
        return 'Musique';
      case 'image':
        return 'Image';
      case 'subtitles':
        return 'Sous-titres';
      default:
        return 'Générique';
    }
  });

  protected startEditingTitle(): void {
    this.isEditingTitle.set(true);
  }

  protected commitTitle(value: string): void {
    const trimmed = value.trim();
    if (trimmed && trimmed !== this.title()) {
      this.titleChange.emit(trimmed);
    }
    this.isEditingTitle.set(false);
  }

  protected cancelEditingTitle(): void {
    this.isEditingTitle.set(false);
  }
}
