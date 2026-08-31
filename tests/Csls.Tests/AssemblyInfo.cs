// Integration tests start real editor and build-host process trees. Two workers keep those
// nested processes within hosted-runner resource budgets while retaining method-level coverage.
[assembly: Parallelize(Workers = 2, Scope = ExecutionScope.MethodLevel)]
