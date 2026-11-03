import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { FlowPreviewComponent } from '../../../shared/ui/flow-preview/flow-preview';
import { IconComponent } from '../../../shared/ui/icon.component';
import { AiContextStore } from '../ai-context.store';
import { AiConversationStore } from '../ai-conversation.store';

/** Panneau « Espace de travail » : ressource jointe, outils de lecture, fichiers et aperçu. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-ai-workspace-panel',
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatSelectModule,
    MatProgressSpinnerModule,
    IconComponent,
    FlowPreviewComponent,
  ],
  templateUrl: './ai-workspace-panel.component.html',
  styleUrl: './ai-workspace-panel.component.scss',
})
export class AiWorkspacePanelComponent {
  readonly context = inject(AiContextStore);
  readonly conversations = inject(AiConversationStore);
}
