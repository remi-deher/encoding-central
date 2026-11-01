import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ServerInfo } from '../core/models';
import { ServerService } from '../core/server.service';

@Component({
  selector: 'app-overview-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  templateUrl: './overview-page.html',
})
export class OverviewPage {
  private readonly servers = inject(ServerService);
  protected readonly server = signal<ServerInfo | null>(null);
  protected readonly workerCount = signal(0);
  protected readonly error = signal('');

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      const [server, workers] = await Promise.all([this.servers.get(), this.servers.workers()]);
      this.server.set(server);
      this.workerCount.set(workers.length);
    } catch {
      this.error.set('Le serveur n’a pas répondu.');
    }
  }
}
