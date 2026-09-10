import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

/** Owns isolated build outputs for the test explorer's serialized discovery operations. */
export class TestDiscoveryWorkspace {
  private readonly directories = new Map<string, string>();

  async getProjectDirectory(projectPath: string): Promise<string> {
    const existing = this.directories.get(projectPath);
    if (existing !== undefined) {
      return existing;
    }

    const directory = await mkdtemp(join(tmpdir(), "csls-test-discovery-"));
    this.directories.set(projectPath, directory);
    return directory;
  }

  /** Releases only owned temporary directories after the explorer's operations have drained. */
  async dispose(): Promise<void> {
    await Promise.all([...this.directories.values()].map((directory) =>
      rm(directory, { force: true, recursive: true }),
    ));
    this.directories.clear();
  }
}
