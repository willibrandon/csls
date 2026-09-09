using Csls.Debugger;
using Csls.Debugger.StackProbe;
using System.Globalization;

if (args is not [string root, string mode, string offset, string checkpoint])
{
    throw new ArgumentException("Expected repository root, probe mode, offset, and checkpoint.");
}

DebuggerWorkerEnvironment.InitializeCurrentProcess();
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(50));
if (mode.StartsWith("values-", StringComparison.Ordinal))
{
    await ValueProgressProbe.RunAsync(root, mode[7..], int.Parse(checkpoint, CultureInfo.InvariantCulture),
        lifetime.Token).ConfigureAwait(false);
}
else
{
    await StackProgressProbe.RunAsync(root, mode, int.Parse(offset, CultureInfo.InvariantCulture),
        int.Parse(checkpoint, CultureInfo.InvariantCulture), lifetime.Token).ConfigureAwait(false);
}
