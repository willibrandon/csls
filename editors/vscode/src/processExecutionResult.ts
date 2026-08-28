export interface ProcessExecutionResult {
  readonly exitCode: number | null;
  readonly stderr: string;
  readonly stdout: string;
}
