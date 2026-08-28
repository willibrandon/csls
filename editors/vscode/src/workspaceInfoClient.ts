export interface WorkspaceInfoClient {
  sendRequest<TResult>(method: string): Promise<TResult>;
}
