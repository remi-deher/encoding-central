import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, finalize, shareReplay } from 'rxjs';
import {
  AiClusterView,
  AiClusterModelCatalog,
  AiModelPolicy,
  AiClusterQuotas,
  AiClusterUsage,
  AiSessionPage,
  AiSessionState,
  AiAssistantConversation,
  AiAssistantConversationSearchResult,
  AiAssistantOptions,
  AiAssistantConversationDetail,
  AiAssistantActivity,
  AiAssistantMessageRequest,
  AiAssistantMessageResponse,
  AiAssistantConfirmActionRequest,
  AiAssistantPrepareActionRequest,
  AiAssistantPreparedAction,
  AiAssistantSourcesRequest,
  AiSettings,
  AiSettingsUpdateRequest,
  AiToolInfo,
  DeviceAuthStartResponse,
  DeviceAuthStatus,
  FlowAiExplainRequest,
  FlowAiGenerateRequest,
  FlowAiRefactorRequest,
  FlowAiResponse,
} from '../models/ai-tools';

@Injectable({
  providedIn: 'root',
})
export class AiToolsService {
  private readonly http = inject(HttpClient);
  private readonly modelCatalogRequests = new Map<string, Observable<AiClusterModelCatalog>>();
  private readonly assistantOptionsRequests = new Map<string, Observable<AiAssistantOptions>>();

  private shareInFlight<T>(
    requests: Map<string, Observable<T>>,
    key: string,
    requestFactory: () => Observable<T>,
  ): Observable<T> {
    const existing = requests.get(key);
    if (existing) return existing;
    let shared!: Observable<T>;
    shared = requestFactory().pipe(
      finalize(() => {
        if (requests.get(key) === shared) requests.delete(key);
      }),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
    requests.set(key, shared);
    return shared;
  }

  listAssistantConversations(): Observable<readonly AiAssistantConversation[]> {
    return this.http.get<readonly AiAssistantConversation[]>(
      '/api/v1/tools/ai/assistant/conversations',
    );
  }

  searchAssistantConversations(query: string): Observable<readonly AiAssistantConversationSearchResult[]> {
    return this.http.get<readonly AiAssistantConversationSearchResult[]>(
      `/api/v1/tools/ai/assistant/conversations/search?q=${encodeURIComponent(query)}`,
    );
  }

  getModelCatalog(): Observable<AiClusterModelCatalog> {
    return this.shareInFlight(this.modelCatalogRequests, 'cluster', () =>
      this.http.get<AiClusterModelCatalog>('/api/v1/tools/ai/models'),
    );
  }

  updateModelPolicy(provider: string, model: string, enabled: boolean): Observable<AiModelPolicy> {
    return this.http.put<AiModelPolicy>('/api/v1/tools/ai/models/policy', {
      provider,
      model,
      enabled,
    });
  }

  getAssistantOptions(): Observable<AiAssistantOptions> {
    return this.shareInFlight(this.assistantOptionsRequests, 'current', () =>
      this.http.get<AiAssistantOptions>('/api/v1/tools/ai/assistant/options'),
    );
  }

  listAssistantActivity(): Observable<readonly AiAssistantActivity[]> {
    return this.http.get<readonly AiAssistantActivity[]>('/api/v1/tools/ai/assistant/activity');
  }

  getAssistantActivity(requestId: string): Observable<AiAssistantActivity | null> {
    return this.http.get<AiAssistantActivity | null>(
      `/api/v1/tools/ai/assistant/activity/${encodeURIComponent(requestId)}`,
    );
  }

  getAssistantConversation(id: string): Observable<AiAssistantConversationDetail> {
    return this.http.get<AiAssistantConversationDetail>(
      `/api/v1/tools/ai/assistant/conversations/${encodeURIComponent(id)}`,
    );
  }

  deleteAssistantConversation(id: string): Observable<void> {
    return this.http.delete<void>(
      `/api/v1/tools/ai/assistant/conversations/${encodeURIComponent(id)}`,
    );
  }

  renameAssistantConversation(id: string, title: string): Observable<AiAssistantConversation> {
    return this.http.put<AiAssistantConversation>(
      `/api/v1/tools/ai/assistant/conversations/${encodeURIComponent(id)}/title`,
      { title },
    );
  }

  updateAssistantSources(
    id: string,
    request: AiAssistantSourcesRequest,
  ): Observable<AiAssistantConversation> {
    return this.http.put<AiAssistantConversation>(
      `/api/v1/tools/ai/assistant/conversations/${encodeURIComponent(id)}/sources`,
      request,
    );
  }

  sendAssistantMessage(request: AiAssistantMessageRequest): Observable<AiAssistantMessageResponse> {
    return this.http.post<AiAssistantMessageResponse>(
      '/api/v1/tools/ai/assistant/messages',
      request,
    );
  }

  cancelAssistantRequest(requestId: string, conversationId?: string | null): Observable<void> {
    const query = conversationId ? `?conversationId=${encodeURIComponent(conversationId)}` : '';
    return this.http.post<void>(
      `/api/v1/tools/ai/assistant/requests/${encodeURIComponent(requestId)}/cancel${query}`,
      {},
    );
  }

  prepareAssistantFlowDeletion(
    request: AiAssistantPrepareActionRequest,
  ): Observable<AiAssistantPreparedAction> {
    return this.http.post<AiAssistantPreparedAction>(
      '/api/v1/tools/ai/assistant/actions/delete-flow/prepare',
      request,
    );
  }

  prepareAssistantFlowPublish(
    request: AiAssistantPrepareActionRequest,
  ): Observable<AiAssistantPreparedAction> {
    return this.http.post<AiAssistantPreparedAction>(
      '/api/v1/tools/ai/assistant/actions/publish-flow/prepare',
      request,
    );
  }

  confirmAssistantFlowPublish(
    request: AiAssistantConfirmActionRequest,
  ): Observable<{ readonly published: boolean }> {
    return this.http.post<{ readonly published: boolean }>(
      '/api/v1/tools/ai/assistant/actions/publish-flow/confirm',
      request,
    );
  }

  confirmAssistantFlowDeletion(
    request: AiAssistantConfirmActionRequest,
  ): Observable<{ readonly deleted: boolean }> {
    return this.http.post<{ readonly deleted: boolean }>(
      '/api/v1/tools/ai/assistant/actions/delete-flow/confirm',
      request,
    );
  }

  prepareAssistantExecutionCancellation(
    request: AiAssistantPrepareActionRequest,
  ): Observable<AiAssistantPreparedAction> {
    return this.http.post<AiAssistantPreparedAction>(
      '/api/v1/tools/ai/assistant/actions/cancel-execution/prepare',
      request,
    );
  }

  prepareAssistantLibraryStart(
    request: AiAssistantPrepareActionRequest,
  ): Observable<AiAssistantPreparedAction> {
    return this.http.post<AiAssistantPreparedAction>(
      '/api/v1/tools/ai/assistant/actions/start-library/prepare',
      request,
    );
  }

  confirmAssistantLibraryStart(
    request: AiAssistantConfirmActionRequest,
  ): Observable<{ readonly queued: number; readonly skipped: number }> {
    return this.http.post<{ readonly queued: number; readonly skipped: number }>(
      '/api/v1/tools/ai/assistant/actions/start-library/confirm',
      request,
    );
  }

  confirmAssistantExecutionCancellation(
    request: AiAssistantConfirmActionRequest,
  ): Observable<{ readonly cancellationRequested: boolean }> {
    return this.http.post<{ readonly cancellationRequested: boolean }>(
      '/api/v1/tools/ai/assistant/actions/cancel-execution/confirm',
      request,
    );
  }

  confirmAssistantLibraryFlowAssignment(
    request: AiAssistantConfirmActionRequest,
  ): Observable<{ readonly assigned: boolean }> {
    return this.http.post<{ readonly assigned: boolean }>(
      '/api/v1/tools/ai/assistant/actions/assign-library-flow/confirm',
      request,
    );
  }

  confirmAssistantFlowTemplateCreation(
    request: AiAssistantConfirmActionRequest,
  ): Observable<{ readonly created: boolean }> {
    return this.http.post<{ readonly created: boolean }>(
      '/api/v1/tools/ai/assistant/actions/create-flow-template/confirm',
      request,
    );
  }

  confirmAssistantBusinessOperation(
    request: AiAssistantConfirmActionRequest,
  ): Observable<{ readonly applied: boolean }> {
    return this.http.post<{ readonly applied: boolean }>(
      '/api/v1/tools/ai/assistant/actions/business-operation/confirm',
      request,
    );
  }

  recordFlowProposalDecision(
    requestId: string,
    decision: 'applied-to-editor' | 'rejected',
  ): Observable<unknown> {
    return this.http.post(
      `/api/v1/tools/ai/assistant/activity/${encodeURIComponent(requestId)}/proposal`,
      { decision },
    );
  }

  listSessions(offset = 0): Observable<AiSessionPage> {
    return this.http.get<AiSessionPage>('/api/v1/tools/ai/sessions', { params: { offset } });
  }

  getSession(id: string): Observable<AiSessionState> {
    return this.http.get<AiSessionState>(`/api/v1/tools/ai/sessions/${encodeURIComponent(id)}`);
  }

  forkSession(id: string): Observable<{ readonly id: string }> {
    return this.http.post<{ readonly id: string }>(
      `/api/v1/tools/ai/sessions/${encodeURIComponent(id)}/fork`,
      {},
    );
  }

  getQuotas(): Observable<AiClusterQuotas> {
    return this.http.get<AiClusterQuotas>('/api/v1/tools/ai/quotas');
  }
  getUsage(days: number): Observable<AiClusterUsage> {
    return this.http.get<AiClusterUsage>(`/api/v1/tools/ai/usage?days=${days}`);
  }

  getCluster(): Observable<AiClusterView> {
    return this.http.get<AiClusterView>('/api/v1/tools/ai/cluster');
  }

  submitAuthInput(toolId: string, input: string, attemptId?: string | null): Observable<{ readonly accepted: boolean }> {
    return this.http.post<{ readonly accepted: boolean }>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/input`,
      { input, attemptId },
    );
  }

  cancelAuth(toolId: string, attemptId?: string | null): Observable<unknown> {
    return this.http.post(`/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/cancel`, { attemptId });
  }

  listTools(): Observable<readonly AiToolInfo[]> {
    return this.http.get<readonly AiToolInfo[]>('/api/v1/tools/ai');
  }

  getToolStatus(toolId: string): Observable<DeviceAuthStatus> {
    return this.http.get<DeviceAuthStatus>(`/api/v1/tools/ai/${encodeURIComponent(toolId)}/status`);
  }

  startDeviceAuth(toolId: string): Observable<DeviceAuthStartResponse> {
    return this.http.post<DeviceAuthStartResponse>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/device-code/start`,
      {},
    );
  }

  getDeviceAuthStatus(toolId: string): Observable<DeviceAuthStatus> {
    return this.http.get<DeviceAuthStatus>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/device-code/status`,
    );
  }

  revokeAuth(toolId: string): Observable<{ readonly revoked: boolean }> {
    return this.http.delete<{ readonly revoked: boolean }>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth`,
    );
  }

  generateFlow(request: FlowAiGenerateRequest): Observable<FlowAiResponse> {
    return this.http.post<FlowAiResponse>('/api/v1/flows/ai/generate', request);
  }

  refactorFlow(request: FlowAiRefactorRequest): Observable<FlowAiResponse> {
    return this.http.post<FlowAiResponse>('/api/v1/flows/ai/refactor', request);
  }

  explainFlow(request: FlowAiExplainRequest): Observable<FlowAiResponse> {
    return this.http.post<FlowAiResponse>('/api/v1/flows/ai/explain', request);
  }

  getSettings(): Observable<AiSettings> {
    return this.http.get<AiSettings>('/api/v1/tools/ai/settings');
  }

  getSettingsModelCatalog(): Observable<AiClusterModelCatalog> {
    return this.getModelCatalog();
  }

  updateSettings(request: AiSettingsUpdateRequest): Observable<AiSettings> {
    return this.http.put<AiSettings>('/api/v1/tools/ai/settings', request);
  }
}
