import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatMenuModule } from '@angular/material/menu';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';
import { AiContextStore } from '../ai-context.store';
import { AiConversationStore } from '../ai-conversation.store';
import { IconComponent } from '../../../shared/ui/icon.component';

/** Barre des ressources jointes à la conversation, avec le menu d’actions par source. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-ai-source-bar',
  imports: [MatButtonModule, MatChipsModule, MatMenuModule, RouterLink, IconComponent],
  template: `
    @if (conversations.detail(); as current) {
      @if (conversations.visibleConversationSources(current.conversation).length) {
        <div class="source-bar">
          <mat-chip-set aria-label="Ressources jointes">
            @for (
              source of conversations.visibleConversationSources(current.conversation);
              track source.kind + source.resourceId
            ) {
              <span class="source-item"
                ><mat-chip>{{ source.label }}</mat-chip
                ><button
                  matIconButton
                  [matMenuTriggerFor]="sourceMenu"
                  [attr.aria-label]="'Actions pour ' + source.label"
                >
                  <app-icon name="more_vert" /></button
              ></span>
              <mat-menu #sourceMenu="matMenu">
                <a mat-menu-item [routerLink]="source.href">Ouvrir la ressource</a>
                @if (isAdmin() && source.kind === 'flow' && source.version) {
                  @if (context.hasDraftFor(source)) {
                    <button
                      mat-menu-item
                      [disabled]="conversations.actionBusy()"
                      (click)="context.publishFlowFromAssistant(source, isAdmin())"
                    >
                      Publier le brouillon…
                    </button>
                  }
                  <button
                    mat-menu-item
                    [disabled]="conversations.actionBusy()"
                    (click)="context.deleteFlowFromAssistant(source, isAdmin())"
                  >
                    Supprimer le flow…
                  </button>
                }
                @if (isAdmin() && context.canStartLibrary(source)) {
                  <button
                    mat-menu-item
                    [disabled]="conversations.actionBusy()"
                    (click)="context.startLibraryFromAssistant(source, isAdmin())"
                  >
                    Démarrer les exécutions…
                  </button>
                }
                @if (isAdmin() && context.isCancelableExecution(source)) {
                  <button
                    mat-menu-item
                    [disabled]="conversations.actionBusy()"
                    (click)="context.cancelExecutionFromAssistant(source, isAdmin())"
                  >
                    Annuler l’exécution…
                  </button>
                }
                @if (conversations.conversationSources(current.conversation).length > 1) {
                  <button
                    mat-menu-item
                    [disabled]="
                      conversations.sending() ||
                      context.updatingSources() ||
                      conversations.hasPendingRequest()
                    "
                    (click)="context.removeConversationSource(source)"
                  >
                    Retirer
                  </button>
                }
              </mat-menu>
            }
          </mat-chip-set>
        </div>
      }
    }
  `,
  styleUrl: './ai-source-bar.component.scss',
})
export class AiSourceBarComponent {
  readonly conversations = inject(AiConversationStore);
  readonly context = inject(AiContextStore);
  protected readonly isAdmin = inject(AuthService).isAdmin;
}
