import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { UserAccount, UserRole } from '../models/auth';

@Injectable({ providedIn: 'root' })
export class UserAccounts {
  private readonly http = inject(HttpClient);

  list() {
    return this.http.get<readonly UserAccount[]>('/api/v1/users');
  }
  create(username: string, password: string, role: UserRole) {
    return this.http.post<UserAccount>('/api/v1/users', { username, password, role });
  }
  update(id: string, role: UserRole, isActive: boolean) {
    return this.http.put<void>('/api/v1/users/' + id, { role, isActive });
  }
  resetPassword(id: string, newPassword: string) {
    return this.http.put<void>('/api/v1/users/' + id + '/password', { newPassword });
  }
  remove(id: string) {
    return this.http.delete<void>('/api/v1/users/' + id);
  }
}
