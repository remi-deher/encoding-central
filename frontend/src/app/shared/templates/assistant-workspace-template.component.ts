import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatSidenavModule } from '@angular/material/sidenav';

/** Cadre commun de conversation, historique et contexte pour le rôle Assister. */
@Component({
  selector: 'app-assistant-workspace-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatSidenavModule],
  host: {
    '[class.assistant-panel-mode]': 'panelMode()',
  },
  template: `
    <mat-drawer-container
      class="assistant-layout"
      [hasBackdrop]="compact() || panelMode()"
      (backdropClick)="backdropClicked.emit()"
    >
      <mat-drawer
        class="conversation-panel"
        [mode]="compact() ? 'over' : 'side'"
        [opened]="!panelMode() && historyOpen()"
        (closedStart)="historyClosed.emit()"
        aria-label="Historique des conversations"
      >
        <ng-content select="[assistant-history]" />
      </mat-drawer>
      <mat-drawer
        class="preview-panel"
        position="end"
        [mode]="compact() || panelMode() ? 'over' : 'side'"
        [opened]="previewOpen()"
        (closedStart)="previewClosed.emit()"
        aria-label="Espace de travail de l’assistant"
      >
        <ng-content select="[assistant-context]" />
      </mat-drawer>
      <mat-drawer-content class="chat-panel">
        <ng-content select="[assistant-chat]" />
      </mat-drawer-content>
    </mat-drawer-container>
  `,
  styleUrl: './assistant-workspace-template.component.scss',
})
export class AssistantWorkspaceTemplateComponent {
  readonly compact = input(false);
  readonly panelMode = input(false);
  readonly historyOpen = input(false);
  readonly previewOpen = input(false);

  readonly backdropClicked = output<void>();
  readonly historyClosed = output<void>();
  readonly previewClosed = output<void>();
}
