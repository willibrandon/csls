export interface MtpTestListDocument {
  readonly schemaVersion: number;
  readonly tests: readonly {
    readonly displayName: string;
    readonly location?: {
      readonly file?: string;
      readonly lineEnd?: number;
      readonly lineStart?: number;
    };
    readonly type?: {
      readonly methodName?: string;
      readonly namespace?: string;
      readonly typeName?: string;
    };
    readonly uid: string;
  }[];
}
