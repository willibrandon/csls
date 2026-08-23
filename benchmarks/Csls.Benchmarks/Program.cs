using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

Summary[] summaries = [.. BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args)];
return summaries.Length != 0 && summaries.All(summary =>
    !summary.HasCriticalValidationErrors &&
    summary.Reports.Any() &&
    summary.Reports.All(report => report.Success))
    ? 0
    : 1;
