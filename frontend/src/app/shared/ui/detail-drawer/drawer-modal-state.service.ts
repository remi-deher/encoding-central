import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class DrawerModalState {
  private readonly document = inject(DOCUMENT);
  private readonly owners = new Set<object>();

  acquire(owner: object): () => void {
    this.owners.add(owner);
    this.sync();
    return () => {
      this.owners.delete(owner);
      this.sync();
    };
  }

  private sync(): void {
    const opened = this.owners.size > 0;
    this.document.body?.classList.toggle('detail-drawer-open', opened);
    this.document.querySelector('.application-sidebar')?.toggleAttribute('inert', opened);
  }
}
