export interface McpToolPropertySchema {
  readonly type?: string;
  readonly description?: string;
  readonly default?: unknown;
  readonly enum?: readonly string[];
}

export interface McpToolInputSchema {
  readonly type?: string;
  readonly properties?: Record<string, McpToolPropertySchema>;
  readonly required?: readonly string[];
}

export interface McpToolDefinition {
  readonly name: string;
  readonly description: string;
  readonly inputSchema: McpToolInputSchema;
}

export interface McpToolsListResponse {
  readonly tools: readonly McpToolDefinition[];
}
