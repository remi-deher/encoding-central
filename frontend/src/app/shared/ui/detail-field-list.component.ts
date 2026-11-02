import { ChangeDetectionStrategy, Component, computed, input, signal, TemplateRef } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatTabsModule } from '@angular/material/tabs';

export interface DetailField {
  readonly section?: string;
  readonly label: string;
  readonly value: string | number | boolean;
  readonly copiable?: boolean;
  readonly copyValue?: string;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-detail-field-list',
  imports: [MatTabsModule, NgTemplateOutlet],
  template: `
    @if (tabbed() && sections().length > 1) {
      <mat-tab-group class="detail-tabs" [attr.aria-label]="tabsLabel()">
        @for (section of sections(); track section.title) {
          <mat-tab [label]="section.title || 'Général'">
            @if (sectionTemplates()[section.title]; as template) {
              <ng-container [ngTemplateOutlet]="template" />
            } @else {
              <dl class="detail-field-list tab-fields">
                @for (field of section.fields; track field.label) {
                  <div>
                    <dt>{{ field.label }}</dt>
                    <dd class="detail-value-wrapper">
                      <span>{{ field.value }}</span>
                      @if (field.copiable) {
                        <button
                          type="button"
                          class="detail-copy-button"
                          [attr.aria-label]="'Copier ' + field.label"
                          (click)="copyToClipboard(fieldCopyValue(field))"
                        >
                          <svg viewBox="0 0 24 24" class="copy-icon" aria-hidden="true">
                            <path
                              fill="currentColor"
                              d="M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z"
                            />
                          </svg>
                          @if (copiedField() === fieldCopyValue(field)) {
                            <span class="copy-feedback">Copié</span>
                          }
                        </button>
                      }
                    </dd>
                  </div>
                }
              </dl>
            }
          </mat-tab>
        }
      </mat-tab-group>
    } @else {
      <dl
        class="detail-field-list"
        [class.detail-field-list_cards]="layout() === 'cards' || layout() === 'horizontal-cards'"
        [class.detail-field-list_horizontal-cards]="layout() === 'horizontal-cards'"
      >
        @for (section of sections(); track section.title) {
          <section class="detail-section">
            @if (section.title) {
              <h3>{{ section.title }}</h3>
            }
            @for (field of section.fields; track field.label) {
              <div>
                <dt>{{ field.label }}</dt>
                <dd class="detail-value-wrapper">
                  <span>{{ field.value }}</span>
                  @if (field.copiable) {
                    <button
                      type="button"
                      class="detail-copy-button"
                      [attr.aria-label]="'Copier ' + field.label"
                      (click)="copyToClipboard(fieldCopyValue(field))"
                    >
                      <svg viewBox="0 0 24 24" class="copy-icon" aria-hidden="true">
                        <path
                          fill="currentColor"
                          d="M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z"
                        />
                      </svg>
                      @if (copiedField() === fieldCopyValue(field)) {
                        <span class="copy-feedback">Copié</span>
                      }
                    </button>
                  }
                </dd>
              </div>
            }
          </section>
        }
      </dl>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .detail-field-list.detail-field-list_cards {
      margin-bottom: 24px;
    }
    .detail-field-list_horizontal-cards {
      display: flex;
      align-items: stretch;
      gap: 12px;
      overflow-x: auto;
      padding: 2px 2px 12px;
      scrollbar-gutter: stable;
      overscroll-behavior-inline: contain;
    }
    .detail-field-list_horizontal-cards .detail-section {
      display: contents;
    }
    .detail-field-list_horizontal-cards .detail-section h3 {
      flex: 0 0 100%;
    }
    .detail-field-list_horizontal-cards .detail-section > div {
      min-width: 260px;
      flex: 0 0 min(360px, 78vw);
    }
    .detail-field-list_cards .detail-section {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
    }
    .detail-field-list_cards .detail-section h3 {
      grid-column: 1/-1;
      margin: 4px 0;
    }
    .detail-field-list_cards .detail-section > div {
      padding: 16px;
      border: 1px solid var(--border-subtle);
      border-radius: 12px;
      background: var(--surface);
    }
    .detail-field-list_cards .detail-section dd {
      font-size: 18px;
      font-weight: 600;
    }
    @media (max-width: 600px) {
      .detail-field-list_cards .detail-section {
        grid-template-columns: 1fr;
      }
    }

    .detail-tabs {
      display: block;
    }

    .tab-fields {
      min-height: 120px;
      padding: 18px 4px 4px;
      gap: 9px;
    }

    .detail-field-list {
      margin: 0;
      display: grid;
      gap: 22px;
    }

    .detail-section {
      display: grid;
      gap: 14px;
    }

    .tab-fields > div {
      padding: 12px 14px;
      border: 1px solid var(--border-subtle);
      border-radius: 9px;
      background: var(--surface-muted);
    }

    .detail-section h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: 14px;
      font-weight: 700;
    }

    .detail-field-list div {
      display: grid;
      gap: 5px;
    }

    .detail-field-list dt {
      color: var(--text-muted);
      font-size: 14px;
      font-weight: 700;
      letter-spacing: 0.035em;
      text-transform: uppercase;
    }

    .detail-field-list dd {
      margin: 0;
      color: var(--text-primary);
      font-size: 14px;
      line-height: 1.5;
      overflow-wrap: anywhere;
      white-space: pre-line;
    }

    .detail-value-wrapper {
      display: inline-flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 8px;
    }

    .detail-copy-button {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 3px 6px;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-sm);
      background: var(--surface-container-low);
      color: var(--text-muted);
      cursor: pointer;
      font-size: 11px;
      line-height: 1;
      transition: all 0.15s ease-in-out;

      &:hover {
        background: var(--surface-container);
        color: var(--text-primary);
        border-color: var(--border-default);
      }
    }

    .copy-icon {
      width: 14px;
      height: 14px;
    }

    .copy-feedback {
      font-weight: 600;
      color: var(--color-success, #10b981);
    }
  `,
})
export class DetailFieldListComponent {
  readonly fields = input.required<readonly DetailField[]>();
  readonly layout = input<'list' | 'cards' | 'horizontal-cards'>('list');
  readonly tabbed = input(false);
  readonly tabsLabel = input('Sections du détail');
  readonly sectionTemplates = input<Readonly<Record<string, TemplateRef<unknown>>>>({});

  protected readonly copiedField = signal<string | null>(null);

  protected fieldCopyValue(field: DetailField): string {
    return field.copyValue ?? String(field.value);
  }

  protected copyToClipboard(val: string): void {
    if (!val || val === '—') return;
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      void navigator.clipboard.writeText(val);
    }
    this.copiedField.set(val);
    setTimeout(() => {
      if (this.copiedField() === val) {
        this.copiedField.set(null);
      }
    }, 2000);
  }

  protected readonly sections = computed(() => {
    const sections = new Map<string, DetailField[]>();
    for (const field of this.fields()) {
      const title = field.section ?? '';
      const fields = sections.get(title) ?? [];
      fields.push(field);
      sections.set(title, fields);
    }
    return Array.from(sections, ([title, fields]) => ({ title, fields }));
  });
}
