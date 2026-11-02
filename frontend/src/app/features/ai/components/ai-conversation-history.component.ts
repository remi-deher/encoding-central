import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import {
  AiAssistantConversation,
  AiNodeAvailability,
} from '../../../shared/models/ai-tools';

@Component({
  selector: 'app-ai-conversation-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressSpinnerModule],
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
          <span class="conversation-count" aria-label="Nombre de conversations">{{ totalCount() }}</span>
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
                  <span class="conversation-mark" aria-hidden="true">✦</span>
                  <span class="conversation-copy">
                    <strong>{{ conversation.title }}</strong>
                    <span class="conversation-meta">
                      <span>{{ conversation.source.label }}</span>
                      <span aria-hidden="true">·</span>
                      <time [attr.datetime]="conversation.updatedAt">{{ timeLabel(conversation.updatedAt) }}</time>
                    </span>
                    @if (coordinatorLabel(conversation); as origin) {
                      <span class="node-origin">{{ origin }}</span>
                    }
                  </span>
                </button>
                <button
                  class="conversation-delete"
                  mat-icon-button
                  type="button"
                  [disabled]="sending() || deletingId() !== null"
                  [attr.aria-label]="'Supprimer la conversation ' + conversation.title"
                  title="Supprimer la conversation"
                  (click)="deleteRequested.emit(conversation)"
                >
                  @if (deletingId() === conversation.id) {
                    <mat-spinner diameter="16" />
                  } @else {
                    <svg
                      viewBox="0 0 24 24"
                      width="18"
                      height="18"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="1.8"
                      aria-hidden="true"
                    >
                      <path
                        d="M4 7h16M10 11v6M14 11v6M5 7l1 14h12l1-14M9 7V4h6v3"
                        stroke-linecap="round"
                        stroke-linejoin="round"
                      />
                    </svg>
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
  readonly nodes = input<readonly AiNodeAvailability[]>([]);
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

  protected timeLabel(updatedAt: string): string {
    const date = new Date(updatedAt);
    if (Number.isNaN(date.getTime())) return '';
    const options: Intl.DateTimeFormatOptions =
      this.dateGroup(updatedAt) === 'Aujourd’hui'
        ? { hour: '2-digit', minute: '2-digit' }
        : { day: 'numeric', month: 'short' };
    return new Intl.DateTimeFormat('fr-FR', options).format(date);
  }

  protected coordinatorLabel(conversation: AiAssistantConversation): string | null {
    if (!conversation.coordinatorId) return null;
    const node = this.nodes().find((candidate) => candidate.nodeId === conversation.coordinatorId);
    return node ? `Discussion du nœud ${node.name}` : 'Discussion d’un autre nœud';
  }
}
