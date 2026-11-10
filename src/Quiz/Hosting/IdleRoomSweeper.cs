using Microsoft.AspNetCore.SignalR;
using Quiz.Game;
using Quiz.Hubs;
using Quiz.Pairing;

namespace Quiz.Hosting;

/// <summary>
/// Once a minute, drops rooms nobody has touched for longer than the idle timeout — and ends each one
/// the way the host closing it would, so the phones of an abandoned room are told it closed instead of
/// being left on a dead question, and nothing is still listening to a code that is free again. It also
/// frees the pairing codes nobody claimed in time.
/// </summary>
public sealed class IdleRoomSweeper(
    RoomStore rooms,
    IHubContext<GameHub> hub,
    ConnectionRooms connections,
    PairingStore pairings,
    ILogger<IdleRoomSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var expired = pairings.SweepExpired();
                    if (expired > 0) log.LogInformation("Swept {Count} expired pairing code(s)", expired);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Expired codes are refused on access anyway, so a failed sweep costs memory, not
                    // correctness; an exception escaping here would end the server.
                    log.LogError(error, "Could not sweep expired pairing codes");
                }

                var removed = rooms.SweepIdle();
                foreach (var code in removed)
                {
                    try
                    {
                        await GameHub.Ended(hub.Clients, hub.Groups, connections, code);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        // That room is already out of the store, so there is nothing to put back and
                        // nothing to retry; the rooms after it still have to be told. An exception
                        // escaping here would end this service, and with the default
                        // BackgroundServiceExceptionBehavior that ends the server.
                        log.LogError(error, "Could not tell room {Code} that it closed", code);
                    }
                }
                if (removed.Count > 0) log.LogInformation("Swept {Count} idle room(s)", removed.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
