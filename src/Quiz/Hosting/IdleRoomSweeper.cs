using Quiz.Game;

namespace Quiz.Hosting;

/// <summary>Once a minute, drops rooms nobody has touched for longer than the idle timeout.</summary>
public sealed class IdleRoomSweeper(RoomStore rooms, ILogger<IdleRoomSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var removed = rooms.SweepIdle();
                if (removed > 0) log.LogInformation("Swept {Count} idle room(s)", removed);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
