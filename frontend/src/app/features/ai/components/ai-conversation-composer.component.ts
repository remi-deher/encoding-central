import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { IconComponent } from '../../../shared/ui/icon.component';
import { AiAssistantModelOption } from '../../../shared/models/ai-tools';
import { AiModelSelectorComponent } from './ai-model-selector.component';

export const AI_ASSISTANT_FILE_ACCEPT =
  'Dockerfile,Makefile,.env,.txt,.md,.markdown,.rst,.csv,.tsv,.log,.out,.json,.jsonc,.json5,.yaml,.yml,.xml,.toml,.ini,.conf,.config,.properties,.srt,.ass,.ssa,.vtt,.sub,.smi,.sami,.ttml,.dfxp,.mpl,.lrc,.sbv,.ffmetadata,.ffpreset,.ffscript,.ffconcat,.filter,.m3u,.m3u8,.nfo,.prx,.avs,.avsi,.vpy,.qpfile,.chapters,.edl,.cue,.xspf,.pls,.mpd,.csproj,.sln,.props,.targets,.editorconfig,.gitignore,.cs,.fs,.vb,.ts,.tsx,.js,.jsx,.mjs,.cjs,.py,.pyw,.sh,.bash,.zsh,.ps1,.psm1,.bat,.cmd,.sql,.html,.htm,.css,.scss,.less,.proto,.graphql,.gql';

@Component({
  selector: 'app-ai-conversation-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatMenuModule,
    IconComponent,
    AiModelSelectorComponent,
  ],
  template: `
    <form class="composer" (ngSubmit)="send.emit()">
      <div class="prompt-area">
        @if (attachmentName()) {
          <div class="file-attachment">
            <span aria-hidden="true">📎</span>
            <span class="file-name">{{ attachmentName() }}</span>
            <button
              mat-icon-button
              type="button"
              aria-label="Retirer le fichier joint"
              [disabled]="locked()"
              (click)="clearAttachment()"
            >
              ×
            </button>
          </div>
        }
        <textarea
          class="prompt-input"
          name="prompt"
          aria-label="Votre question"
          placeholder="Envoyer un message à l’Assistant"
          rows="2"
          maxlength="4000"
          [ngModel]="text()"
          (ngModelChange)="text.set($event)"
          [disabled]="locked()"
          (keydown)="onKeydown($event)"
        ></textarea>
      </div>
      <div class="composer-tools">
        <div class="composer-attachments">
          <button
            mat-icon-button
            type="button"
            class="attach-button"
            aria-label="Joindre un fichier ou une ressource"
            title="Joindre un fichier ou une ressource"
            [disabled]="locked()"
            [matMenuTriggerFor]="attachMenu"
          >
            <app-icon name="add" />
          </button>
          <span class="composer-hint">Ajouter un fichier ou une ressource</span>
        </div>
        <mat-menu #attachMenu="matMenu">
          <button mat-menu-item type="button" (click)="fileInput.click()">
            <app-icon name="attach" />
            <span>Joindre un fichier</span>
          </button>
          <button mat-menu-item type="button" (click)="resourceChosen.emit('flow')">
            <app-icon name="flow" />
            <span>Un flow</span>
          </button>
          <button mat-menu-item type="button" (click)="resourceChosen.emit('execution')">
            <app-icon name="history" />
            <span>Une exécution</span>
          </button>
          <button mat-menu-item type="button" (click)="resourceChosen.emit('node')">
            <app-icon name="server" />
            <span>Un nœud</span>
          </button>
          <button mat-menu-item type="button" (click)="resourceChosen.emit('library')">
            <app-icon name="library" />
            <span>Une bibliothèque</span>
          </button>
        </mat-menu>
        <input
          #fileInput
          class="visually-hidden"
          type="file"
          multiple
          [accept]="fileAccept"
          aria-label="Choisir des fichiers texte à joindre"
          (change)="filesChosen.emit($event)"
        />
        <app-ai-model-selector
          [options]="options()"
          [selectedKey]="selectedKey()"
          [fallbackProvider]="provider()"
          [unavailableModel]="unavailableModel()"
          [effort]="effort()"
          [disabled]="locked() || loading()"
          (typeChange)="typeChange.emit($event)"
          (modelChange)="modelChange.emit($event)"
          (effortChange)="effortChange.emit($event)"
          (settingsOpenChange)="settingsOpenChange.emit($event)"
        />
        @if (busy()) {
          <button
            mat-mini-fab
            class="send-button"
            type="button"
            aria-label="Interrompre"
            [disabled]="cancelling()"
            (click)="cancel.emit()"
          >
            <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor" aria-hidden="true">
              <rect x="6" y="6" width="12" height="12" rx="2" />
            </svg>
          </button>
        } @else {
          <button
            mat-mini-fab
            class="send-button"
            type="submit"
            [attr.aria-label]="resuming() ? 'Reprendre la demande' : 'Envoyer'"
            [disabled]="!canSend()"
          >
            <svg
              viewBox="0 0 24 24"
              width="20"
              height="20"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              aria-hidden="true"
            >
              <path d="M12 19V5m-6 6 6-6 6 6" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
        }
      </div>
    </form>
  `,
  styles: `
    :host {
      display: block;
      box-sizing: border-box;
      width: 100%;
      max-width: 100%;
      min-width: 0;
      container-type: inline-size;
    }
    .composer {
      box-sizing: border-box;
      display: grid;
      gap: var(--space-2);
      width: 100%;
      min-width: 0;
      padding: var(--space-3) var(--space-4);
      border: 1px solid var(--outline-variant);
      border-radius: var(--radius-2xl);
      background: var(--surface-container);
      box-shadow: var(--elevation-1);
      transition:
        border-color 160ms var(--motion-emphasized),
        box-shadow 160ms var(--motion-emphasized),
        background-color 160ms var(--motion-emphasized);
    }
    .composer:focus-within {
      border-color: var(--accent-strong);
      background: var(--surface-container-high);
      box-shadow: 0 0 0 1px var(--accent-strong), var(--elevation-1);
    }
    .prompt-area {
      display: grid;
      gap: var(--space-1);
      min-width: 0;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      padding: 0;
      margin: -1px;
      overflow: hidden;
      clip: rect(0, 0, 0, 0);
      white-space: nowrap;
      border: 0;
    }
    .file-attachment {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      width: fit-content;
      max-width: 100%;
      margin: 0;
      padding: 0 var(--space-2);
      border-radius: var(--radius-md);
      background: var(--surface-container-highest);
    }
    .file-name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: var(--font-label);
    }
    .file-attachment button {
      flex: 0 0 auto;
    }
    .prompt-input {
      display: block;
      width: 100%;
      box-sizing: border-box;
      min-height: var(--touch-target);
      max-height: calc(var(--space-6) + var(--space-6));
      padding: var(--space-2) 0;
      border: 0;
      resize: none;
      overflow-y: auto;
      background: transparent;
      color: var(--text-primary);
      font: inherit;
      line-height: 1.5;
      outline: none;
    }
    .prompt-input::placeholder {
      color: var(--text-muted);
      opacity: 1;
    }
    .prompt-input:disabled {
      opacity: 0.7;
    }
    .composer-tools {
      display: flex;
      align-items: center;
      min-width: 0;
      gap: var(--space-2);
    }
    .composer-attachments {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      min-width: 0;
    }
    .attach-button {
      width: var(--touch-target);
      height: var(--touch-target);
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
    }
    .composer-hint {
      overflow: hidden;
      color: var(--text-muted);
      font-size: var(--font-label);
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    app-ai-model-selector {
      flex: 0 1 auto;
      min-width: 0;
      margin-left: auto;
    }
    .send-button {
      flex: 0 0 var(--touch-target);
      width: var(--touch-target);
      height: var(--touch-target);
      margin-left: 0;
      background: var(--accent-strong);
      color: var(--on-accent, #fff);
    }
    @container (max-width: 560px) {
      .composer-tools {
        flex-wrap: wrap;
      }
      .composer-attachments {
        flex: 1 1 auto;
      }
      app-ai-model-selector {
        margin-left: auto;
      }
    }
    @container (max-width: 420px) {
      .composer-tools {
        display: grid;
        grid-template-columns: minmax(0, 1fr) var(--touch-target);
        gap: var(--space-1);
      }
      app-ai-model-selector {
        grid-column: 1 / -1;
        grid-row: 1;
        margin: 0 0 var(--space-1);
      }
      .composer-attachments { grid-column: 1; grid-row: 2; }
      .send-button {
        grid-column: 2;
        grid-row: 2;
      }
      .composer-hint { display: none; }
      .composer { padding: var(--space-2) var(--space-3); }
    }
    @media (prefers-reduced-motion: reduce) {
      .composer { transition: none; }
    }
  `,
})
export class AiConversationComposerComponent {
  readonly text = model('');
  readonly options = input<readonly AiAssistantModelOption[]>([]);
  readonly selectedKey = input('');
  readonly provider = input('');
  readonly unavailableModel = input<string | null>(null);
  readonly effort = input<string | null>(null);
  readonly locked = input(false);
  readonly loading = input(false);
  readonly busy = input(false);
  readonly cancelling = input(false);
  readonly resuming = input(false);
  readonly canSend = input(false);
  readonly attachmentName = input<string | null>(null);
  readonly typeChange = output<string>();
  readonly modelChange = output<string>();
  readonly effortChange = output<string | null>();
  readonly settingsOpenChange = output<boolean>();
  readonly send = output<void>();
  readonly cancel = output<void>();
  readonly resourceChosen = output<'flow' | 'execution' | 'node' | 'library'>();
  readonly fileRemoved = output<void>();
  readonly filesChosen = output<Event>();
  protected readonly fileAccept = AI_ASSISTANT_FILE_ACCEPT;
  protected clearAttachment(): void {
    this.fileRemoved.emit();
  }
  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      if (this.canSend()) this.send.emit();
    }
  }
}
