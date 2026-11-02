import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  CreateFlowTemplateFromFlowRequest,
  FlowTemplateNodeValues,
  FlowTemplateDetails,
  FlowTemplateSummary,
  UpdateFlowTemplateRequest,
} from '../models/flow-templates';
import type { FlowDocument } from '../models/flow-validation';

@Injectable({ providedIn: 'root' })
export class FlowTemplates {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/templates';

  list() {
    return this.http.get<readonly FlowTemplateSummary[]>(this.base);
  }

  get(id: string) {
    return firstValueFrom(this.http.get<FlowTemplateDetails>(`${this.base}/${id}`));
  }

  createFromFlow(request: CreateFlowTemplateFromFlowRequest) {
    return firstValueFrom(this.http.post<FlowTemplateDetails>(`${this.base}/from-flow`, request));
  }

  update(id: string, request: UpdateFlowTemplateRequest) {
    return firstValueFrom(this.http.put<FlowTemplateDetails>(`${this.base}/${id}`, request));
  }

  createFlow(
    id: string,
    name: string,
    templateRevision: number,
    configuration: readonly FlowTemplateNodeValues[],
  ) {
    return firstValueFrom(
      this.http.post<{ definition: FlowDocument }>(`/api/v1/flows/from-template/${id}`, {
        name,
        templateRevision,
        configuration,
      }),
    );
  }
}
