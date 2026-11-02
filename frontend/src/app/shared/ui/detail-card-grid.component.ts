import {
  ChangeDetectionStrategy,
  Component,
  input,
  OnChanges,
  signal,
  TemplateRef,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { CdkAccordion, CdkAccordionItem } from '@angular/cdk/accordion';

export type DetailCardTone = 'neutral' | 'accent' | 'success' | 'warning';
export type DetailCardIcon =
  'video' | 'image' | 'audio' | 'plugin' | 'tool' | 'server' | 'activity' | 'system' | 'library';

export interface DetailCardTag {
  readonly label: string;
  readonly tone?: DetailCardTone;
}

export interface DetailCardDetail {
  readonly label: string;
  readonly description?: string;
  readonly tone?: DetailCardTone;
}

export interface DetailCardSection {
  readonly title: string;
  readonly items: readonly DetailCardDetail[];
}

export interface DetailCard {
  readonly id: string;
  readonly title: string;
  readonly subtitle: string;
  readonly icon: DetailCardIcon;
  readonly status?: DetailCardTag;
  readonly tags: readonly DetailCardTag[];
  readonly sections: readonly DetailCardSection[];
  readonly initiallyExpanded?: boolean;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-detail-card-grid',
  imports: [CdkAccordion, CdkAccordionItem, NgTemplateOutlet],
  template: `
    @if (cards().length) {
      <div
        class="detail-card-grid"
        cdkAccordion
        [multi]="true"
        role="group"
        [attr.aria-label]="ariaLabel()"
      >
        @for (card of cards(); track card.id; let index = $index) {
          <article
            class="detail-card"
            [class.detail-card_expanded]="!collapsible() || expandedState(card.id)"
            cdkAccordionItem
            #item="cdkAccordionItem"
            [expanded]="!collapsible() || expandedState(card.id)"
          >
            <h3 class="detail-card-heading">
              <button
                class="detail-card-toggle"
                type="button"
                [disabled]="!collapsible()"
                [id]="domId + '-heading-' + index"
                [attr.aria-expanded]="!collapsible() || expandedState(card.id)"
                [attr.aria-controls]="domId + '-content-' + index"
                (click)="toggle(item, card.id)"
              >
                <span class="detail-card-icon" aria-hidden="true">
                  @switch (card.icon) {
                    @case ('video') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <rect x="3" y="5" width="18" height="14" rx="2" />
                        <path class="icon-solid" d="m10 9 5 3-5 3z" />
                      </svg>
                    }
                    @case ('image') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <rect x="3" y="4" width="18" height="16" rx="2" />
                        <circle cx="9" cy="9" r="1.5" />
                        <path d="m4 17 5-5 3 3 3-4 5 6" />
                      </svg>
                    }
                    @case ('audio') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path d="M4 10v4h4l5 4V6l-5 4z" />
                        <path d="M16 9a5 5 0 0 1 0 6M18.5 6.5a9 9 0 0 1 0 11" />
                      </svg>
                    }
                    @case ('plugin') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path
                          d="M9 4a2 2 0 1 1 4 0v2h4a2 2 0 0 1 2 2v3h-2a2 2 0 1 0 0 4h2v3a2 2 0 0 1-2 2h-4v-2a2 2 0 1 0-4 0v2H6a2 2 0 0 1-2-2v-4h2a2 2 0 1 0 0-4H4V8a2 2 0 0 1 2-2h3z"
                        />
                      </svg>
                    }
                    @case ('server') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <rect x="4" y="4" width="16" height="6" rx="1.5" />
                        <rect x="4" y="14" width="16" height="6" rx="1.5" />
                        <path d="M8 7h.01M8 17h.01M12 7h5M12 17h5" />
                      </svg>
                    }
                    @case ('activity') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path d="M3 12h4l3-7 4 14 3-7h4" />
                      </svg>
                    }
                    @case ('system') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <rect x="7" y="7" width="10" height="10" rx="2" />
                        <path d="M9 1v4m6-4v4M9 19v4m6-4v4M1 9h4m-4 6h4m14-6h4m-4 6h4" />
                      </svg>
                    }
                    @case ('library') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path d="M4 5h16v15H4zM8 5v15m8-15v15M2 3h20" />
                      </svg>
                    }
                    @default {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path d="M4 5h16v3H4zM4 10h16v3H4zM4 15h10v3H4z" />
                      </svg>
                    }
                  }
                </span>
                <span class="detail-card-title-group">
                  <span class="detail-card-title">{{ card.title }}</span>
                  <span class="detail-card-subtitle">{{ card.subtitle }}</span>
                </span>
                @if (card.status; as status) {
                  <span
                    class="detail-card-status"
                    [class]="'detail-card-status detail-card-status_' + status.tone"
                  >
                    {{ status.label }}
                  </span>
                }
                @if (collapsible()) {
                  <span
                    class="detail-card-chevron"
                    [class.detail-card-chevron_open]="expandedState(card.id)"
                    aria-hidden="true"
                    >⌄</span
                  >
                }
              </button>
            </h3>

            @if (card.tags.length) {
              <div class="detail-card-tags" aria-label="Résumé des capacités">
                @for (tag of card.tags; track tag.label) {
                  <span
                    class="detail-card-tag"
                    [class]="'detail-card-tag detail-card-tag_' + tag.tone"
                    >{{ tag.label }}</span
                  >
                }
              </div>
            }

            @if (!collapsible() || expandedState(card.id)) {
              <div
                class="detail-card-content"
                role="region"
                [id]="domId + '-content-' + index"
                [attr.aria-labelledby]="domId + '-heading-' + index"
              >
                @if (cardContentTemplates()[card.id]; as contentTemplate) {
                  <ng-container [ngTemplateOutlet]="contentTemplate" />
                } @else {
                  @for (section of card.sections; track section.title) {
                    <section class="detail-card-section">
                      <h4>{{ section.title }}</h4>
                      <ul>
                        @for (detail of section.items; track detail.label) {
                          <li>
                            <span>{{ detail.label }}</span>
                            @if (detail.description) {
                              <span
                                class="detail-card-detail"
                                [class]="'detail-card-detail detail-card-detail_' + detail.tone"
                                >{{ detail.description }}</span
                              >
                            }
                          </li>
                        }
                      </ul>
                    </section>
                  }
                  @if (!card.sections.length) {
                    <p class="detail-card-empty">Aucun détail supplémentaire déclaré.</p>
                  }
                }
              </div>
            }
          </article>
        }
      </div>
    } @else {
      <p class="detail-card-empty-state" role="status">{{ emptyMessage() }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .detail-card-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 230px), 1fr));
      align-items: start;
      gap: 12px;
    }
    .detail-card {
      min-width: 0;
      overflow: hidden;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-lg);
      background: var(--surface-container-low);
      transition: border-color 120ms ease, box-shadow 120ms ease;
    }
    .detail-card_expanded {
      grid-column: 1 / -1;
      border-color: color-mix(in srgb, var(--accent) 38%, var(--border-subtle));
      box-shadow: var(--elevation-1);
    }
    .detail-card-heading {
      margin: 0;
    }
    .detail-card-toggle {
      width: 100%;
      min-height: 72px;
      padding: 12px 14px;
      display: flex;
      align-items: center;
      gap: 12px;
      border: 0;
      background: transparent;
      color: var(--text-primary);
      text-align: left;
      cursor: pointer;
      font: inherit;
    }
    .detail-card-toggle:hover {
      background: var(--surface-muted);
    }
    .detail-card-toggle:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: -2px;
    }
    .detail-card-icon {
      width: 40px;
      height: 40px;
      display: grid;
      place-items: center;
      flex: 0 0 auto;
      border-radius: var(--radius-md, 12px);
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .detail-card-icon svg {
      width: 20px;
      height: 20px;
      fill: none;
      stroke: currentColor;
      stroke-linecap: round;
      stroke-linejoin: round;
      stroke-width: 1.7;
    }
    .detail-card-icon svg .icon-solid {
      fill: currentColor;
      stroke: none;
    }
    .detail-card-title-group {
      min-width: 0;
      flex: 1;
      display: grid;
      gap: 4px;
    }
    .detail-card-title {
      overflow: hidden;
      font-size: var(--font-title-sm, 15px);
      font-weight: 700;
      letter-spacing: -0.01em;
      text-overflow: ellipsis;
      white-space: nowrap;
      color: var(--text-primary);
    }
    .detail-card-subtitle {
      overflow: hidden;
      color: var(--text-muted);
      font-size: var(--font-label, 12px);
      font-weight: 400;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .detail-card-status,
    .detail-card-tag {
      max-width: 100%;
      width: fit-content;
      padding: 4px 8px;
      border-radius: var(--radius-xs, 6px);
      background: var(--surface-muted);
      color: var(--text-secondary);
      font-size: var(--font-label, 12px);
      font-weight: 600;
      line-height: 1.25;
    }
    .detail-card-status_success, .detail-card-tag_success, .detail-card-detail_success {
      background: var(--success-soft);
      color: var(--success);
    }
    .detail-card-status_warning, .detail-card-tag_warning, .detail-card-detail_warning {
      background: var(--warning-soft);
      color: var(--warning);
    }
    .detail-card-status_accent, .detail-card-tag_accent, .detail-card-detail_accent {
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .detail-card-chevron {
      flex: 0 0 auto;
      color: var(--text-muted);
      font-size: 17px;
      transition: transform 120ms ease;
    }
    .detail-card-chevron_open {
      transform: rotate(180deg);
    }
    .detail-card-tags {
      padding: 0 14px 12px 66px;
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .detail-card-content {
      padding: var(--space-4, 16px);
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 200px), 1fr));
      gap: var(--space-4, 16px);
      border-top: 1px solid var(--border-subtle);
    }
    .detail-card-section h4 {
      margin: 0 0 var(--space-2, 8px);
      color: var(--text-muted);
      font-size: var(--font-label, 12px);
      font-weight: 700;
      letter-spacing: 0.06em;
      text-transform: uppercase;
    }
    .detail-card-section ul {
      margin: 0;
      padding: 0;
      display: grid;
      gap: var(--space-2, 8px);
      list-style: none;
    }
    .detail-card-section li {
      min-width: 0;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      font-size: 13px;
      line-height: 1.45;
      color: var(--text-secondary);
    }
    .detail-card-section li > span:first-child {
      overflow-wrap: anywhere;
    }
    .detail-card-detail {
      padding: 3px 8px;
      border-radius: var(--radius-xs, 6px);
      background: var(--surface-muted);
      color: var(--text-primary);
      font-size: var(--font-label, 12px);
      font-weight: 600;
      text-align: right;
    }
    .detail-card-empty,
    .detail-card-empty-state {
      margin: 0;
      color: var(--text-muted);
      font-size: var(--font-body, 14px);
      line-height: 1.5;
    }
    .detail-card-empty-state {
      padding: var(--space-4, 16px);
      border: 1px dashed var(--border-subtle);
      border-radius: var(--radius-md, 12px);
      background: var(--surface-muted);
    }
    @media (max-width: 600px) {
      .detail-card-grid,
      .detail-card-content {
        grid-template-columns: 1fr;
      }
      .detail-card-status {
        max-width: 100px;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .detail-card,
      .detail-card-chevron {
        transition: none;
      }
    }
  `,
})
export class DetailCardGridComponent implements OnChanges {
  private static nextId = 0;
  protected readonly domId = 'detail-card-grid-' + DetailCardGridComponent.nextId++;
  readonly cards = input.required<readonly DetailCard[]>();
  readonly ariaLabel = input('Détails');
  readonly emptyMessage = input('Aucun élément à afficher.');
  readonly initiallyExpanded = input(false);
  readonly collapsible = input(true);
  readonly cardContentTemplates = input<Readonly<Record<string, TemplateRef<unknown>>>>({});
  private readonly expandedCards = signal<ReadonlyMap<string, boolean>>(new Map());

  ngOnChanges(): void {
    const current = this.expandedCards();
    const next = new Map(
      this.cards().map((card) => [
        card.id,
        current.get(card.id) ?? card.initiallyExpanded ?? this.initiallyExpanded(),
      ]),
    );
    if (
      next.size !== current.size ||
      [...next].some(([cardId, expanded]) => current.get(cardId) !== expanded)
    ) {
      this.expandedCards.set(next);
    }
  }

  protected expandedState(cardId: string): boolean {
    return this.expandedCards().get(cardId) ?? this.initiallyExpanded();
  }

  protected toggle(item: CdkAccordionItem, cardId: string): void {
    if (!this.collapsible()) return;
    item.toggle();
    this.expandedCards.update((current) => new Map(current).set(cardId, item.expanded));
  }
}
