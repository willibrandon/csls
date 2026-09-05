using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csls.Tests;

/// <summary>
/// Verifies event-driven debugger resources through current MCP subscriptions.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task<T> AssertResourceSubscriptionAsync<T>(
        McpClient client,
        string resourceUri,
        Func<Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        var acknowledged = new TaskCompletionSource<JsonRpcNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource<JsonRpcNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IAsyncDisposable acknowledgementRegistration = client.RegisterNotificationHandler(
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            (notification, _) =>
            {
                acknowledged.TrySetResult(notification);
                return default;
            });
        await using ConfiguredAsyncDisposable acknowledgementCleanup =
            acknowledgementRegistration.ConfigureAwait(false);
        IAsyncDisposable updateRegistration = client.RegisterNotificationHandler(
            NotificationMethods.ResourceUpdatedNotification,
            (notification, _) =>
            {
                if ((notification.Params as JsonObject)?["uri"]?.GetValue<string>() ==
                    resourceUri)
                {
                    updated.TrySetResult(notification);
                }

                return default;
            });
        await using ConfiguredAsyncDisposable updateCleanup =
            updateRegistration.ConfigureAwait(false);
        using var listenCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var subscriptionId = new RequestId($"debugger-{Guid.NewGuid():N}");
        string malformedResourceUri = $"{resourceUri}/not-a-resource";
        string unownedResourceUri = $"csls://debug/session/{Guid.NewGuid():N}";
        Task listenTask = client.SendRequestAsync(
            new JsonRpcRequest
            {
                Id = subscriptionId,
                Method = RequestMethods.SubscriptionsListen,
                Params = JsonSerializer.SerializeToNode(
                    new SubscriptionsListenRequestParams
                    {
                        Notifications = new SubscriptionsListenNotifications
                        {
                            ResourceSubscriptions =
                            [
                                resourceUri,
                                malformedResourceUri,
                                unownedResourceUri
                            ]
                        }
                    })
            },
            listenCancellation.Token);

        try
        {
            JsonRpcNotification acknowledgement = await acknowledged.Task.WaitAsync(
                cancellationToken).ConfigureAwait(false);
            JsonArray granted = acknowledgement.Params!["notifications"]!
                ["resourceSubscriptions"]!.AsArray();
            Assert.HasCount(1, granted);
            Assert.AreEqual(resourceUri, granted[0]!.GetValue<string>());
            string? acknowledgedId = GetSubscriptionId(acknowledgement);
            Assert.IsNotNull(acknowledgedId);

            T result = await mutation().ConfigureAwait(false);
            JsonRpcNotification notification = await updated.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(acknowledgedId, GetSubscriptionId(notification));
            return result;
        }
        finally
        {
            await client.SendMessageAsync(
                new JsonRpcNotification
                {
                    Method = NotificationMethods.CancelledNotification,
                    Params = JsonSerializer.SerializeToNode(
                        new CancelledNotificationParams { RequestId = subscriptionId })
                },
                CancellationToken.None).ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing);
            await listenCancellation.CancelAsync().ConfigureAwait(false);
            await listenTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static string? GetSubscriptionId(JsonRpcNotification notification) =>
        ((notification.Params as JsonObject)?["_meta"] as JsonObject)?
            [MetaKeys.SubscriptionId]?.ToJsonString();
}
