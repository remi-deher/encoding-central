import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Matériel détecté, présenté à l'identique par les assistants du central et du worker. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-detected-configuration',
  template: `
    <section class="detected-configuration" aria-labelledby="detected-configuration-heading">
      <div class="detected-heading">
        <span class="live-dot" aria-hidden="true"></span>
        <h2 id="detected-configuration-heading">Configuration détectée</h2>
      </div>
      <dl class="detected-grid">
        <div>
          <dt>Système</dt>
          <dd>{{ system() }}</dd>
        </div>
        <div>
          <dt>CPU</dt>
          <dd>{{ cpu() }}</dd>
        </div>
        <div>
          <dt>RAM</dt>
          <dd>{{ memory() }}</dd>
        </div>
        <div>
          <dt>GPU</dt>
          <dd>{{ gpu() }}</dd>
        </div>
        <div>
          <dt>Accélération GPU</dt>
          <dd>{{ acceleration() }}</dd>
        </div>
        <div>
          <dt>{{ addressLabel() }}</dt>
          <dd>{{ address() }}</dd>
        </div>
        @if (interfaces() !== null) {
          <div class="detected-wide">
            <dt>Interfaces</dt>
            <dd>
              @if (interfaces()!.length) {
                <ul class="network-addresses" aria-label="Adresses des interfaces">
                  @for (item of interfaces()!; track item) {
                    <li>{{ item }}</li>
                  }
                </ul>
              } @else {
                {{ interfacesEmpty() }}
              }
            </dd>
          </div>
        }
      </dl>
    </section>
  `,
  styles: `
    .detected-configuration {
      display: grid;
      gap: var(--space-3);
      padding: var(--space-4);
      border-radius: var(--radius-xl);
      background: var(--surface-container-low);
    }
    .detected-heading {
      display: flex;
      align-items: flex-start;
      gap: var(--space-3);
    }
    .detected-heading h2 {
      margin: 2px 0 0;
      font-size: var(--font-title-md);
    }
    .live-dot {
      width: 10px;
      height: 10px;
      flex: 0 0 auto;
      margin-top: 5px;
      border-radius: 50%;
      background: var(--success);
      box-shadow: 0 0 0 4px var(--success-soft);
    }
    .detected-grid {
      display: grid;
      margin: 0;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: var(--space-2);
    }
    .detected-grid > div {
      display: grid;
      gap: 2px;
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }
    .detected-wide {
      grid-column: 1 / -1;
    }
    dt {
      color: var(--text-muted);
      font-size: 12px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }
    dd {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-body);
      font-weight: 700;
      overflow-wrap: anywhere;
    }
    .network-addresses {
      display: grid;
      gap: var(--space-1);
      margin: 0;
      padding: 0;
      list-style: none;
      font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
      font-weight: 400;
    }
    @media (max-width: 560px) {
      .detected-grid {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }
    }
    @media (max-width: 380px) {
      .detected-grid {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class DetectedConfigurationComponent {
  readonly system = input.required<string>();
  readonly cpu = input.required<string>();
  readonly memory = input.required<string>();
  readonly gpu = input.required<string>();
  readonly acceleration = input.required<string>();
  readonly address = input.required<string>();
  readonly addressLabel = input('Adresse IP / accès');
  /** `null` : la liste des interfaces n'est pas affichée (worker). */
  readonly interfaces = input<readonly string[] | null>(null);
  readonly interfacesEmpty = input('Aucune adresse détectée');
}
