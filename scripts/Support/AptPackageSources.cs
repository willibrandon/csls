using System.Diagnostics;

namespace Csls.Support;

/// <summary>
/// Selects an explicit APT source file while preserving package authenticity checks.
/// </summary>
internal static class AptPackageSources
{
    /// <summary>
    /// Appends source selection to an APT command after its optional privilege wrapper.
    /// </summary>
    internal static void Configure(ProcessStartInfo startInfo, string executable, string? sourceList)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (executable is not ("apt-get" or "apt-cache") || sourceList is null)
        {
            return;
        }

        if (!Path.IsPathFullyQualified(sourceList) ||
            Path.GetExtension(sourceList) is not (".list" or ".sources"))
        {
            throw new ArgumentException("CSLS_APT_SOURCE_LIST must name an absolute .list or .sources file.", nameof(sourceList));
        }

        if (!File.Exists(sourceList))
        {
            throw new FileNotFoundException("The selected APT source file does not exist.", sourceList);
        }

        startInfo.ArgumentList.Add("--option");
        startInfo.ArgumentList.Add($"Dir::Etc::sourcelist={sourceList}");
        startInfo.ArgumentList.Add("--option");
        startInfo.ArgumentList.Add("Dir::Etc::sourceparts=-");
    }
}
