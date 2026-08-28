export interface TrxTestResult {
  readonly durationMilliseconds: number | undefined;
  readonly outcome: string;
  readonly testId: string;
  readonly testName: string;
}
