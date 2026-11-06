import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AiAssistantConversation } from '../../../shared/models/ai-tools';
import { IconComponent } from '../../../shared/ui/icon.component';
import { AppDatePipe } from '../../../shared/pipes/app-date.pipe';

@Component({
  selector: 'app-ai-conversation-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    IconComponent,
    AppDatePipe,
  ],
  template: `
    <section
      assistant-history
      [id]="historyId()"
      class="assistant-history-content"
      aria-labelledby="assistant-history-title"
    >
      <header class="conversation-heading">
        <div class="conversation-heading__title">
          <h2 id="assistant-history-title">Conversations</h2>
          <span class="conversation-count" aria-label="Nombre de conversations">{{
            totalCount()
          }}</span>
        </div>
      </header>
      @if (loading()) {
        <p class="history-status muted" role="status">Chargement des conversations…</p>
      } @else if (searching() && !conversations().length) {
        <p class="history-status muted" role="status">Recherche dans les messages…</p>
      } @else if (query().trim() && !conversations().length) {
        <p class="history-status muted">Aucune conversation ne correspond à « {{ query() }} ».</p>
      } @else if (!conversations().length) {
        <p class="history-status muted">Aucune conversation pour le moment.</p>
      }
      <nav class="conversation-list" aria-label="Conversations IA">
        @for (group of groups(); track group.label) {
          <section class="history-group" [attr.aria-label]="group.label">
            <h3>{{ group.label }}</h3>
            @for (conversation of group.items; track conversation.id) {
              <article class="conversation-item" [class.is-active]="activeId() === conversation.id">
                <button
                  class="conversation-link"
                  type="button"
                  [class.is-active]="activeId() === conversation.id"
                  [disabled]="sending() || deletingId() === conversation.id"
                  (click)="conversationSelected.emit(conversation.id)"
                >
                  <app-icon class="conversation-mark" name="auto_awesome" />
                  <span class="conversation-copy">
                    <strong>{{ conversation.title }}</strong>
                    <span class="conversation-meta">
                      <span>{{ conversation.source.label }}</span>
                      <span aria-hidden="true">·</span>
                      <time [attr.datetime]="conversation.updatedAt">{{
                        conversation.updatedAt | appDate: 'relative'
                      }}</time>
                    </span>
                  </span>
                </button>
                <button
                  class="conversation-delete"
                  matIconButton
                  type="button"
                  [disabled]="sending() || deletingId() !== null"
                  [attr.aria-label]="'Supprimer la conversation ' + conversation.title"
                  matTooltip="Supprimer la conversation"
                  (click)="deleteRequested.emit(conversation)"
                >
                  @if (deletingId() === conversation.id) {
                    <mat-spinner diameter="16" />
                  } @else {
                    <app-icon name="delete" />
                  }
                </button>
              </article>
            }
          </section>
        }
      </nav>
    </section>
  `,
  styleUrl: './ai-conversation-history.component.scss',
})
export class AiConversationHistoryComponent {
  readonly historyId = input('assistant-conversation-history');
  readonly conversations = input<readonly AiAssistantConversation[]>([]);
  readonly totalCount = input(0);
  readonly activeId = input<string | null>(null);
  readonly deletingId = input<string | null>(null);
  readonly query = input('');
  readonly loading = input(false);
  readonly searching = input(false);
  readonly sending = input(false);

  readonly conversationSelected = output<string>();
  readonly deleteRequested = output<AiAssistantConversation>();

  protected readonly groups = computed(() => {
    const groups = new Map<string, AiAssistantConversation[]>();
    for (const conversation of this.conversations()) {
      const label = this.dateGroup(conversation.updatedAt);
      const items = groups.get(label) ?? [];
      items.push(conversation);
      groups.set(label, items);
    }
    return [...groups.entries()].map(([label, items]) => ({ label, items }));
  });

  protected dateGroup(updatedAt: string): string {
    const date = new Date(updatedAt);
    if (Number.isNaN(date.getTime())) return 'Plus anciennes';
    const today = new Date();
    const yesterday = new Date(today);
    yesterday.setDate(today.getDate() - 1);
    if (date.toDateString() === today.toDateString()) return 'Aujourd’hui';
    if (date.toDateString() === yesterday.toDateString()) return 'Hier';
    if (today.getTime() - date.getTime() <= 7 * 24 * 60 * 60 * 1000) return '7 derniers jours';
    return 'Plus anciennes';
  }
}
