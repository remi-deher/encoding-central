import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  CreateFlowComponentPackRequest,
  FlowComponentPackDetails,
  FlowComponentPackSummary,
} from '../models/flow-component-packs';

@Injectable({ providedIn: 'root' })
export class FlowComponentPacks {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/component-packs';

  list() {
    return this.http.get<readonly FlowComponentPackSummary[]>(this.base);
  }

  get(id: string) {
    return firstValueFrom(this.http.get<FlowComponentPackDetails>(`${this.base}/${id}`));
  }

  createFromFlow(request: CreateFlowComponentPackRequest) {
    return firstValueFrom(
      this.http.post<FlowComponentPackDetails>(`${this.base}/from-flow`, request),
    );
  }
}
