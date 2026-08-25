using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

if (!args.Any(static argument =>
        string.Equals(argument, "--artifacts", StringComparison.Ordinal) ||
        argument.StartsWith("--artifacts=", StringComparison.Ordinal)))
{
    args =
    [
        .. args,
        "--artifacts",
        Path.Join(FindRepositoryRoot(), "artifacts", "benchmarks")
    ];
}

Summary[] summaries = [.. BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args)];
return summaries.Length != 0 && summaries.All(summary =>
    !summary.HasCriticalValidationErrors &&
    summary.Reports.Any() &&
    summary.Reports.All(report => report.Success))
    ? 0
    : 1;

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}
