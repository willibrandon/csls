export interface WorkspaceInfo {
  readonly generation: number;
  readonly workspaces: readonly {
    readonly documentCount: number;
    readonly projectCount: number;
    readonly rootPath: string;
    readonly workspaceKind: string;
  }[];
  readonly projects: readonly {
    readonly analyzerPaths: readonly string[];
    readonly documentCount: number;
    readonly filePath?: string;
    readonly id: string;
    readonly language: string;
    readonly name: string;
    readonly projectReferenceIds: readonly string[];
    readonly workspaceRoot: string;
  }[];
  readonly documents: readonly {
    readonly filePath?: string;
    readonly id: string;
    readonly isOpen: boolean;
    readonly name: string;
    readonly projectId: string;
  }[];
}
