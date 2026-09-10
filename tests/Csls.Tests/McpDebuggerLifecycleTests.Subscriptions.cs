using ModelContextProtocol;
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
    /// <summary>
    /// Surfaces a rejected subscription request while the server correctly sends no acknowledgement.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SubscriptionWaitReportsRejectedRequest()
    {
        McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        var notification = new TaskCompletionSource<JsonRpcNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAsyncDisposable registration = mcp.Client.RegisterNotificationHandler(
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            (value, _) =>
            {
                notification.TrySetResult(value);
                return default;
            });
        await using ConfiguredAsyncDisposable registrationCleanup = registration.ConfigureAwait(false);
        Task rejected = mcp.Client.SendRequestAsync(new JsonRpcRequest
        {
            Id = new RequestId($"rejected-{Guid.NewGuid():N}"),
            Method = "subscriptions/not-a-method"
        }, TestContext.CancellationToken);

        McpProtocolException failure = await Assert.ThrowsExactlyAsync<McpProtocolException>(async () =>
            await AwaitSubscriptionNotificationAsync(notification.Task, rejected, TestContext.CancellationToken)
                .WaitAsync(TestContext.CancellationToken)
                .ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(McpErrorCode.MethodNotFound, failure.ErrorCode);
        Assert.IsTrue(rejected.IsFaulted);
        Assert.IsFalse(notification.Task.IsCompleted);
    }

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
            JsonRpcNotification acknowledgement = await AwaitSubscriptionNotificationAsync(
                acknowledged.Task, listenTask, cancellationToken).ConfigureAwait(false);
            JsonArray granted = acknowledgement.Params!["notifications"]!
                ["resourceSubscriptions"]!.AsArray();
            Assert.HasCount(1, granted);
            Assert.AreEqual(resourceUri, granted[0]!.GetValue<string>());
            string? acknowledgedId = GetSubscriptionId(acknowledgement);
            Assert.IsNotNull(acknowledgedId);

            T result = await mutation().ConfigureAwait(false);
            JsonRpcNotification notification = await AwaitSubscriptionNotificationAsync(
                updated.Task, listenTask, cancellationToken).ConfigureAwait(false);
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

    private static async Task<JsonRpcNotification> AwaitSubscriptionNotificationAsync(
        Task<JsonRpcNotification> notification, Task listening, CancellationToken cancellationToken)
    {
        Task completed = await Task.WhenAny(notification, listening).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(completed, listening))
        {
            await listening.WaitAsync(cancellationToken).ConfigureAwait(false);
            Assert.Fail("The subscription ended before its next notification.");
        }
        return await notification.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? GetSubscriptionId(JsonRpcNotification notification) =>
        ((notification.Params as JsonObject)?["_meta"] as JsonObject)?
            [MetaKeys.SubscriptionId]?.ToJsonString();
}
