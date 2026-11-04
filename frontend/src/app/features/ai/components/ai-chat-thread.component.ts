import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  input,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { RouterLink } from '@angular/router';
import { IconComponent } from '../../../shared/ui/icon.component';
import { AiContextStore } from '../ai-context.store';
import { AiConversationStore } from '../ai-conversation.store';
import { AiModelStore } from '../ai-model.store';
import { AuthService } from '../../../core/auth/auth.service';

/** Fil de messages de la conversation : tours, proposition d’action, demande en cours. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-ai-chat-thread',
  imports: [MatButtonModule, MatProgressSpinnerModule, RouterLink, IconComponent],
  host: { '[class.assistant-panel-mode]': 'panelMode()' },
  templateUrl: './ai-chat-thread.component.html',
  styleUrl: './ai-chat-thread.component.scss',
})
export class AiChatThreadComponent {
  readonly panelMode = input(false);
  readonly conversations = inject(AiConversationStore);
  readonly context = inject(AiContextStore);
  readonly models = inject(AiModelStore);
  protected readonly isAdmin = inject(AuthService).isAdmin;

  private readonly messageThread = viewChild<ElementRef<HTMLElement>>('messageThread');

  constructor() {
    afterRenderEffect(() => {
      this.conversations.detail();
      this.conversations.pendingPrompt();
      this.conversations.sending();
      const target = this.conversations.searchTarget();
      const thread = this.messageThread()?.nativeElement;
      if (thread && target) {
        requestAnimationFrame(() => {
          const message = thread.querySelector<HTMLElement>(
            `[data-turn-index="${target.turnIndex}"][data-message-role="${target.role}"]`,
          );
          message?.scrollIntoView({ block: 'center', behavior: 'smooth' });
          this.conversations.highlightedTurn.set(target);
          this.conversations.followTail.set(false);
          this.conversations.searchTarget.set(null);
        });
        return;
      }
      if (thread && this.conversations.followTail()) thread.scrollTop = thread.scrollHeight;
    });
  }

  protected onConfirmAction(): void {
    this.conversations.confirmAiAction(this.isAdmin());
  }

  protected onThreadScroll(): void {
    const thread = this.messageThread()?.nativeElement;
    if (thread)
      this.conversations.followTail.set(
        thread.scrollHeight - thread.clientHeight - thread.scrollTop < 80,
      );
  }
}
