using Csls.Debugger.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Csls.Mcp.Worker;

/// <summary>
/// Streams event-driven debugger resource invalidations through SEP-2575.
/// </summary>
internal sealed class McpDebuggerSubscriptions
{
    private const int MaximumSubscriptions = 128;
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates a subscription handler for one connection-owned debugger broker.
    /// </summary>
    internal McpDebuggerSubscriptions(McpDebuggerSessionBroker broker)
    {
        _broker = broker;
    }

    /// <summary>
    /// Owns one long-lived MCP subscription response stream.
    /// </summary>
    internal async ValueTask<EmptyResult> ListenAsync(
        RequestContext<SubscriptionsListenRequestParams> request,
        CancellationToken cancellationToken)
    {
        string[] subscriptions = SelectSubscriptions(
            request.Params.Notifications.ResourceSubscriptions,
            request.Server.ServerOptions.ResourceCollection);
        var pending = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        void OnChanged(McpDebuggerResourceChange change)
        {
            foreach (string uri in subscriptions)
            {
                if (Matches(uri, change) && pending.TryAdd(uri, 0))
                {
                    signal.Writer.TryWrite(true);
                }
            }
        }

        _broker.ResourceChanged += OnChanged;
        try
        {
            await SendAcknowledgementAsync(request, subscriptions, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await foreach (bool signalValue in signal.Reader.ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    _ = signalValue;
                    foreach (string uri in pending.Keys.Order(StringComparer.Ordinal).ToArray())
                    {
                        if (pending.TryRemove(uri, out _))
                        {
                            await SendResourceChangedAsync(request, uri, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            _broker.ResourceChanged -= OnChanged;
            signal.Writer.TryComplete();
        }

        return new EmptyResult();
    }

    private string[] SelectSubscriptions(
        IList<string>? requested,
        McpServerResourceCollection? resources) => requested?
        .Where(static uri => uri.Length <= 2048)
        .Distinct(StringComparer.Ordinal)
        .Take(MaximumSubscriptions)
        .Where(uri => resources is not null &&
            resources.Any(resource => resource.IsMatch(uri)) &&
            TryGetSession(uri, out string? session) &&
            _broker.OwnsSession(session))
        .ToArray() ?? [];

    private static bool Matches(string uri, McpDebuggerResourceChange change)
    {
        if (!TryGetSession(uri, out string? session) ||
            !string.Equals(session, change.DebugSession, StringComparison.Ordinal))
        {
            return false;
        }

        return change.Kind.HasFlag(DebuggerResourceChangeKind.Session) ||
            change.Kind.HasFlag(DebuggerResourceChangeKind.Output) &&
            new Uri(uri).AbsolutePath.StartsWith("/output/", StringComparison.Ordinal);
    }

    private static bool TryGetSession(string value, out string session)
    {
        session = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != "csls" || uri.Host != "debug")
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !IsResourceGroup(segments[0]))
        {
            return false;
        }

        session = segments[1];
        return true;
    }

    private static bool IsResourceGroup(string value) => value is
        "session" or "output" or "threads" or "stack" or "scopes" or
        "variables" or "modules" or "exception" or "source" or "memory" or
        "disassembly";

    private static Task SendAcknowledgementAsync(
        RequestContext<SubscriptionsListenRequestParams> request,
        string[] subscriptions,
        CancellationToken cancellationToken)
    {
        var granted = new SubscriptionsListenNotifications
        {
            ResourceSubscriptions = subscriptions.Length == 0 ? null : subscriptions
        };
        JsonNode? parameters = JsonSerializer.SerializeToNode(
            new SubscriptionsAcknowledgedNotificationParams { Notifications = granted });
        return SendAsync(
            request,
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            parameters,
            cancellationToken);
    }

    private static Task SendResourceChangedAsync(
        RequestContext<SubscriptionsListenRequestParams> request,
        string uri,
        CancellationToken cancellationToken) => SendAsync(
            request,
            NotificationMethods.ResourceUpdatedNotification,
            new JsonObject { ["uri"] = uri },
            cancellationToken);

    private static Task SendAsync(
        RequestContext<SubscriptionsListenRequestParams> request,
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        JsonObject values = parameters as JsonObject ?? [];
        values["_meta"] = new JsonObject
        {
            [MetaKeys.SubscriptionId] = request.JsonRpcRequest.Id.Id switch
            {
                string text => JsonValue.Create(text),
                long number => JsonValue.Create(number),
                _ => null
            }
        };
        return request.Server.SendMessageAsync(
            new JsonRpcNotification { Method = method, Params = values },
            cancellationToken);
    }
}
