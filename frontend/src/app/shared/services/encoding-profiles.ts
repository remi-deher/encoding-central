import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  EncodingProfileDetails,
  EncodingProfileDraft,
  EncodingProfileSummary,
  EncodingProfileVersion,
  VideoEncoderDescriptor,
} from '../models/encoding-profiles';

@Injectable({ providedIn: 'root' })
export class EncodingProfiles {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/encoding-profiles';

  list() {
    return this.http.get<readonly EncodingProfileSummary[]>(this.base);
  }

  encoders() {
    return this.http.get<readonly VideoEncoderDescriptor[]>(`${this.base}/encoders`);
  }

  details(id: string) {
    return firstValueFrom(this.http.get<EncodingProfileDetails>(`${this.base}/${id}`));
  }

  version(id: string, version: number) {
    return firstValueFrom(
      this.http.get<EncodingProfileVersion>(`${this.base}/${id}/versions/${version}`),
    );
  }

  create(draft: EncodingProfileDraft) {
    return firstValueFrom(this.http.post<EncodingProfileDetails>(this.base, { draft }));
  }

  saveDraft(id: string, expectedRevision: number, draft: EncodingProfileDraft) {
    return firstValueFrom(
      this.http.put<EncodingProfileDetails>(`${this.base}/${id}/draft`, {
        expectedRevision,
        draft,
      }),
    );
  }

  publish(id: string, expectedRevision: number) {
    return firstValueFrom(
      this.http.post<EncodingProfileVersion>(`${this.base}/${id}/publish`, { expectedRevision }),
    );
  }
}
