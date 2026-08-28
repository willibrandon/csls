export interface MsbuildProjectPropertiesDocument {
  readonly Properties: {
    readonly IsTestingPlatformApplication?: string;
    readonly OutputType?: string;
    readonly TargetFramework?: string;
    readonly TargetPath?: string;
  };
}
