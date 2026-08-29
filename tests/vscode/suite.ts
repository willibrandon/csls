import { runFeatureContract } from "./web-suite";

export async function run(): Promise<void> {
  const expectedHost = process.env.CSLS_VSCODE_EXPECTED_HOST === "remote"
    ? "remote"
    : "desktop";
  await runFeatureContract({
    expectedHost,
    requireRuntimeExtension: true,
  });
}
