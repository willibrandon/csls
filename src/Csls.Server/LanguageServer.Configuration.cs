using Csls.Core;
using Csls.Protocol;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    private const string PreferredConfigurationSection = "csls";
    private const string LegacyConfigurationSection = "csharp";

    /// <inheritdoc />
    public Task DidChangeConfigurationAsync(
        DidChangeConfigurationParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _supportsConfigurationPull
            ? PullConfigurationAsync(cancellationToken)
            : ApplyConfigurationAsync(
                ParseConfiguration(parameters.Settings),
                "workspace/didChangeConfiguration",
                cancellationToken);
    }

    private Task<bool> PullConfigurationAsync(CancellationToken cancellationToken)
    {
        return _scheduler.ScheduleAsync(
            "workspace/configuration",
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                JsonElement?[] values;
                try
                {
                    values = await _client.GetConfigurationAsync(
                        new ConfigurationParams
                        {
                            Items =
                            [
                                new ConfigurationItem { Section = LegacyConfigurationSection },
                                new ConfigurationItem { Section = PreferredConfigurationSection }
                            ]
                        },
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogConfigurationPullFailed(exception);
                    throw;
                }

                if (values.Length != 2)
                {
                    throw new InvalidDataException(
                        $"The client returned {values.Length} configuration values for 2 sections.");
                }

                LanguageServerConfiguration configuration = MergeConfiguration(
                    ParseConfigurationSection(values[0], LegacyConfigurationSection),
                    ParseConfigurationSection(values[1], PreferredConfigurationSection));
                bool changed = await _workspaceManager
                    .ConfigureAsync(configuration.EnableAnalyzers, context.CancellationToken)
                    .ConfigureAwait(false);
                _configuration = configuration;
                LogConfigurationApplied(
                    configuration.EnableAnalyzers,
                    configuration.FormatOnSave,
                    changed);
                return changed;
            },
            cancellationToken);
    }

    private Task<bool> ApplyConfigurationAsync(
        LanguageServerConfiguration configuration,
        string requestName,
        CancellationToken cancellationToken)
    {
        return _scheduler.ScheduleAsync(
            requestName,
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                bool changed = await _workspaceManager.ConfigureAsync(
                    configuration.EnableAnalyzers,
                    context.CancellationToken).ConfigureAwait(false);
                _configuration = configuration;
                LogConfigurationApplied(
                    configuration.EnableAnalyzers,
                    configuration.FormatOnSave,
                    changed);
                return changed;
            },
            cancellationToken);
    }

    private static LanguageServerConfiguration ParseConfiguration(JsonElement? settings)
    {
        if (settings is not JsonElement root ||
            root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new LanguageServerConfiguration();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The language-server configuration must be an object.");
        }

        bool hasLegacy = root.TryGetProperty(
            LegacyConfigurationSection,
            out JsonElement legacySection);
        bool hasPreferred = root.TryGetProperty(
            PreferredConfigurationSection,
            out JsonElement preferredSection);
        if (!hasLegacy && !hasPreferred)
        {
            return MergeConfiguration(
                default,
                ParseConfigurationSection(root, PreferredConfigurationSection));
        }

        return MergeConfiguration(
            ParseConfigurationSection(
                hasLegacy ? legacySection : null,
                LegacyConfigurationSection),
            ParseConfigurationSection(
                hasPreferred ? preferredSection : null,
                PreferredConfigurationSection));
    }

    private static (bool? EnableAnalyzers, bool? FormatOnSave) ParseConfigurationSection(
        JsonElement? section,
        string sectionName)
    {
        if (section is not JsonElement value ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {sectionName} configuration must be an object.");
        }

        return (
            ParseBooleanSetting(value, sectionName, "enableAnalyzers"),
            ParseBooleanSetting(value, sectionName, "formatOnSave"));
    }

    private static bool? ParseBooleanSetting(
        JsonElement section,
        string sectionName,
        string settingName)
    {
        if (!section.TryGetProperty(settingName, out JsonElement setting) ||
            setting.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return setting.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"The {sectionName}.{settingName} setting must be a boolean.")
        };
    }

    private static LanguageServerConfiguration MergeConfiguration(
        (bool? EnableAnalyzers, bool? FormatOnSave) legacy,
        (bool? EnableAnalyzers, bool? FormatOnSave) preferred)
    {
        return new LanguageServerConfiguration
        {
            EnableAnalyzers = preferred.EnableAnalyzers ?? legacy.EnableAnalyzers ?? true,
            FormatOnSave = preferred.FormatOnSave ?? legacy.FormatOnSave ?? false
        };
    }

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Applied configuration: analyzer diagnostics enabled={EnableAnalyzers}, format on save={FormatOnSave}, workspace changed={Changed}")]
    private partial void LogConfigurationApplied(
        bool enableAnalyzers,
        bool formatOnSave,
        bool changed);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Client configuration pull failed")]
    private partial void LogConfigurationPullFailed(Exception exception);
}
