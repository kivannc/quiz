using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Quiz.Hubs;

namespace Quiz.Tests;

/// <summary>
/// The point at which a test may count what a connection has received.
///
/// A round trip is not that point by itself. The client hands what the server sends it to a queue
/// of its own and runs the handlers from there, one after another, while the answer to a call is
/// delivered straight away: a call can return with an earlier message still waiting in that queue,
/// its handler not yet run. A test that counts messages right after a round trip is then counting
/// early, and fails now and again on a busy machine for no reason the game has anything to do with.
///
/// So the round trip is followed by a marker sent to this one connection through the server's own
/// hub context. It travels the same road as every other message, behind all of them, and the client
/// runs handlers in the order the messages came: when the marker's handler runs, every handler
/// before it has finished. Nothing in the game knows about the marker; a client ignores a message
/// it has no handler for, and this one is only ever sent to the connection that is waiting for it.
/// </summary>
internal static class Settled
{
    public static async Task After(HubConnection connection, IServiceProvider server, TimeSpan patience)
    {
        // Everything the server sent this connection before now has reached it...
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<string>("Join", "0000", "barrier"));

        // ...and has been handled once the marker behind it has.
        var marker = Guid.NewGuid().ToString("N");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listening = connection.On<string>("test-settled", sent =>
        {
            if (sent == marker) reached.TrySetResult();
        });
        var hub = server.GetRequiredService<IHubContext<GameHub>>();
        await hub.Clients.Client(connection.ConnectionId!).SendAsync("test-settled", marker);
        await reached.Task.WaitAsync(patience);
    }
}
