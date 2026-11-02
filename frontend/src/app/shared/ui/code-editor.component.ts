import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  effect,
  input,
  output,
  viewChild,
} from '@angular/core';
import { basicSetup } from 'codemirror';
import { Compartment, EditorState } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { javascript } from '@codemirror/lang-javascript';
import { python } from '@codemirror/lang-python';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-code-editor',
  template: '<div #host class="code-editor" [class.code-editor_roomy]="roomy()"></div>',
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .code-editor {
      --editor-min-height: 260px;
      --editor-max-height: 560px;
      overflow: hidden;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-md);
    }
    .code-editor_roomy {
      --editor-min-height: 420px;
      --editor-max-height: none;
    }
    .code-editor:focus-within {
      outline: 2px solid var(--accent-strong);
      outline-offset: 2px;
    }
    @media (max-width: 600px) {
      .code-editor {
        --editor-min-height: 220px;
      }
    }
  `,
})
export class CodeEditorComponent implements AfterViewInit, OnDestroy {
  readonly value = input('');
  readonly language = input<'javascript' | 'python'>('javascript');
  readonly disabled = input(false);
  readonly roomy = input(false);
  readonly label = input('Code du script');
  readonly valueChange = output<string>();
  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private readonly languageSlot = new Compartment();
  private readonly editableSlot = new Compartment();
  private readonly labelSlot = new Compartment();
  private view?: EditorView;
  private syncing = false;

  constructor() {
    effect(() => {
      const value = this.value();
      const language = this.language();
      const disabled = this.disabled();
      const label = this.label();
      if (!this.view) return;
      this.syncing = true;
      try {
        this.view.dispatch({
          ...(this.view.state.doc.toString() !== value
            ? { changes: { from: 0, to: this.view.state.doc.length, insert: value } }
            : {}),
          effects: [
            this.languageSlot.reconfigure(this.languageExtension(language)),
            this.editableSlot.reconfigure(this.editableExtension(disabled)),
            this.labelSlot.reconfigure(EditorView.contentAttributes.of({ 'aria-label': label })),
          ],
        });
      } finally {
        this.syncing = false;
      }
    });
  }

  ngAfterViewInit(): void {
    this.view = new EditorView({
      parent: this.host().nativeElement,
      state: EditorState.create({
        doc: this.value(),
        extensions: [
          basicSetup,
          this.languageSlot.of(this.languageExtension(this.language())),
          this.editableSlot.of(this.editableExtension(this.disabled())),
          this.labelSlot.of(EditorView.contentAttributes.of({ 'aria-label': this.label() })),
          EditorView.theme({
            '&': {
              minHeight: 'var(--editor-min-height)',
              maxHeight: 'var(--editor-max-height)',
              backgroundColor: 'var(--surface)',
              color: 'var(--text-primary)',
              fontSize: 'var(--font-body)',
            },
            '.cm-scroller': {
              overflow: 'auto',
              fontFamily: 'ui-monospace, SFMono-Regular, Consolas, monospace',
            },
            '.cm-content': { caretColor: 'var(--text-primary)' },
            '.cm-gutters': {
              color: 'var(--text-muted)',
              backgroundColor: 'var(--surface-muted)',
              borderRight: '1px solid var(--border-subtle)',
            },
            '.cm-activeLineGutter': { color: 'var(--text-primary)' },
            '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--text-primary)' },
            '&.cm-focused .cm-selectionBackground, .cm-selectionBackground': {
              backgroundColor: 'var(--accent-soft)',
            },
          }),
          EditorView.updateListener.of((update) => {
            if (!this.syncing && update.docChanged)
              this.valueChange.emit(update.state.doc.toString());
          }),
        ],
      }),
    });
  }

  ngOnDestroy(): void {
    this.view?.destroy();
  }
  private languageExtension(language: 'javascript' | 'python') {
    return language === 'python' ? python() : javascript();
  }
  private editableExtension(disabled: boolean) {
    return [EditorView.editable.of(!disabled), EditorState.readOnly.of(disabled)];
  }
}
