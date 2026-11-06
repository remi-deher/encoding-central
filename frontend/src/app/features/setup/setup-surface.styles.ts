/** Surface commune des assistants de premier démarrage (central et worker) : une seule source pour éviter toute divergence. */
export const SETUP_SURFACE_STYLES = `
  .progress {
    margin: 0;
    color: var(--text-secondary);
    font-size: var(--font-label);
  }
  .setup-stack,
  .card {
    display: grid;
    gap: var(--space-4);
  }
  .card {
    padding: var(--space-5);
    border-radius: var(--radius-xl);
    background: var(--surface-container);
    box-shadow: var(--elevation-2);
  }
  h2,
  p {
    margin: 0;
  }
  h2 {
    font-size: var(--font-title-lg);
  }
  .heading {
    display: grid;
    gap: var(--space-1);
  }
  .heading p,
  .notice {
    color: var(--text-secondary);
    line-height: 1.5;
  }
  mat-form-field {
    width: 100%;
  }
  .field-hint {
    color: var(--text-secondary);
    font-size: var(--font-label);
  }
  .field-error {
    color: var(--danger);
    font-size: var(--font-label);
  }
  .field-row {
    display: flex;
    gap: var(--space-2);
    align-items: center;
  }
  .field-row mat-form-field {
    flex: 1;
  }
  .notice {
    padding: var(--space-3) var(--space-4);
    border-radius: var(--radius-lg);
    background: var(--surface-container-low);
  }
  .summary {
    display: grid;
    gap: var(--space-3);
    margin: 0;
  }
  .summary > div {
    display: grid;
    grid-template-columns: 9rem minmax(0, 1fr);
    gap: var(--space-3);
  }
  .summary dt {
    color: var(--text-muted);
    font-size: var(--font-label);
    font-weight: 700;
    letter-spacing: 0.04em;
    text-transform: uppercase;
  }
  .summary dd {
    margin: 0;
    color: var(--text-primary);
    font-weight: 700;
    overflow-wrap: anywhere;
  }
  .actions {
    display: flex;
    justify-content: flex-end;
    gap: var(--space-2);
    margin-top: var(--space-2);
  }
  .error {
    margin: 0;
    padding: var(--space-3) var(--space-4);
    border-radius: var(--radius-lg);
    background: var(--danger-soft);
    color: var(--danger);
  }
  @media (max-width: 560px) {
    .card {
      padding: var(--space-4);
    }
    .summary > div {
      grid-template-columns: minmax(0, 1fr);
      gap: var(--space-1);
    }
  }
`;
