import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { catchError, map, Observable, of } from 'rxjs';
import { McpToolDefinition, McpToolsListResponse } from '../models/mcp';

interface JsonRpcResponse<T> {
  readonly jsonrpc: string;
  readonly id: number | string;
  readonly result?: T;
  readonly error?: {
    readonly code: number;
    readonly message: string;
  };
}

@Injectable({ providedIn: 'root' })
export class McpService {
  private readonly http = inject(HttpClient);

  listTools(): Observable<readonly McpToolDefinition[]> {
    return this.http
      .post<JsonRpcResponse<McpToolsListResponse>>('/api/v1/mcp', {
        jsonrpc: '2.0',
        id: 1,
        method: 'tools/list',
      })
      .pipe(
        map((response) => response.result?.tools ?? []),
        catchError((err) => {
          console.error('Failed to list MCP tools:', err);
          return of([]);
        }),
      );
  }

  ping(): Observable<boolean> {
    return this.http
      .post<JsonRpcResponse<Record<string, unknown>>>('/api/v1/mcp', {
        jsonrpc: '2.0',
        id: 1,
        method: 'ping',
      })
      .pipe(
        map((res) => !res.error),
        catchError(() => of(false)),
      );
  }
}
