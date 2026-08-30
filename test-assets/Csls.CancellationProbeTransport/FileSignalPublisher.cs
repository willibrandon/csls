using System.IO;

namespace Csls.Testing;

/// <summary>
/// Publishes complete cross-process file signals through atomic replacement.
/// </summary>
internal static class FileSignalPublisher
{
    private static readonly object s_gate = new();

    /// <summary>
    /// Appends one value and atomically replaces the marker with the complete result.
    /// </summary>
    /// <param name="markerPath">The marker file to replace.</param>
    /// <param name="value">The signal value to append.</param>
    internal static void AppendAllText(string markerPath, string value)
    {
        lock (s_gate)
        {
            string contents = File.Exists(markerPath)
                ? File.ReadAllText(markerPath)
                : string.Empty;
            Publish(markerPath, contents + value);
        }
    }

    private static void Publish(string markerPath, string value)
    {
        string pendingPath = markerPath + ".pending";
        try
        {
            File.WriteAllText(pendingPath, value);
            File.Replace(pendingPath, markerPath, destinationBackupFileName: null);
        }
        finally
        {
            File.Delete(pendingPath);
        }
    }
}
