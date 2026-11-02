import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { USER_ROLE_LABELS } from '../../shared/models/auth';
import { PasswordChangeDialogComponent } from './password-change-dialog.component';
import { AuthService } from './auth.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-user-menu',
  imports: [MatMenuModule, MatTooltipModule],
  template: `
    @if (user(); as current) {
      <button
        type="button"
        class="user-trigger"
        [class.is-collapsed]="collapsed()"
        [matMenuTriggerFor]="menu"
        [attr.aria-label]="'Compte de ' + current.username + ', ' + roleLabel()"
        [matTooltip]="collapsed() ? current.username + ' (' + roleLabel() + ')' : ''"
        matTooltipPosition="right"
      >
        <span class="user-avatar" aria-hidden="true">{{
          current.username.slice(0, 1).toUpperCase()
        }}</span>
        @if (!collapsed()) {
          <span class="user-info">
            <span class="user-name">{{ current.username }}</span>
            <span class="user-role">{{ roleLabel() }}</span>
          </span>
        }
      </button>
      <mat-menu #menu="matMenu" xPosition="before">
        <button mat-menu-item type="button" (click)="changePassword()">
          Changer le mot de passe
        </button>
        <button mat-menu-item type="button" (click)="logout()">Se déconnecter</button>
      </mat-menu>
    }
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
      min-width: 0;
    }
    .user-trigger {
      display: flex;
      flex-direction: row;
      align-items: center;
      gap: 9px;
      width: 100%;
      box-sizing: border-box;
      border: none;
      background: transparent;
      border-radius: var(--radius-lg);
      padding: 5px 6px;
      cursor: pointer;
      text-align: left;
      color: inherit;
      transition: background-color 160ms ease;
    }
    .user-trigger:hover {
      background: var(--sidebar-hover);
    }
    .user-trigger:focus-visible {
      outline: 2px solid var(--sidebar-accent);
      outline-offset: 1px;
    }
    .user-trigger.is-collapsed {
      width: 38px;
      height: 38px;
      min-width: 38px;
      padding: 0;
      justify-content: center;
      margin-inline: auto;
      border-radius: var(--radius-pill);
    }
    .user-avatar {
      width: 30px;
      height: 30px;
      min-width: 30px;
      flex: 0 0 30px;
      display: grid;
      place-items: center;
      border-radius: var(--radius-pill);
      background: var(--sidebar-avatar-background, var(--accent-soft));
      color: var(--sidebar-avatar-text, var(--accent-strong));
      font-size: 13px;
      font-weight: 750;
      line-height: 1;
    }
    .user-info {
      min-width: 0;
      flex: 1;
      display: flex;
      flex-direction: column;
      gap: 1px;
    }
    .user-name {
      font-weight: 650;
      color: var(--sidebar-text-strong);
      font-size: 13px;
      line-height: 1.25;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .user-role {
      color: var(--sidebar-text-muted);
      font-size: 11px;
      line-height: 1.2;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
  `,
})
export class UserMenuComponent {
  readonly collapsed = input(false);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  protected readonly user = this.auth.user;
  protected readonly roleLabel = computed(() => {
    const role = this.user()?.role;
    return role ? USER_ROLE_LABELS[role] : '';
  });

  protected changePassword(): void {
    this.dialog.open(PasswordChangeDialogComponent, { width: '420px', maxWidth: '95vw' });
  }

  protected logout(): void {
    void this.auth.logout();
  }
}
