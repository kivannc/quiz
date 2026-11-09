using System.Text.Json;
using System.Text.Json.Serialization;
using Quiz.Game;
using Quiz.Hosting;
using Quiz.Hubs;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Configuration comes from the environment only. Bad values stop the app from starting rather than starting something broken.
const int MinimumHostKeyLength = 16;
var hostKey = config["QUIZ_HOST_KEY"];
if (string.IsNullOrWhiteSpace(hostKey))
    throw new InvalidOperationException(
        $"QUIZ_HOST_KEY is missing or blank. Set it to a secret of at least {MinimumHostKeyLength} characters; without it nobody could open a room.");
if (hostKey.Length < MinimumHostKeyLength)
    throw new InvalidOperationException(
        $"QUIZ_HOST_KEY is too short ({hostKey.Length} characters). It must be at least {MinimumHostKeyLength} characters, because it is the only thing protecting room creation.");

int Setting(string name, int fallback) => config.GetValue<int?>(name) ?? fallback;

var options = new RoomOptions(
    maxRooms: Setting("QUIZ_MAX_ROOMS", 3),
    maxPlayers: Setting("QUIZ_MAX_PLAYERS", 20),
    idleTimeout: TimeSpan.FromMinutes(Setting("QUIZ_ROOM_IDLE_MINUTES", 60)),
    questionLimit: TimeSpan.FromSeconds(Setting("QUIZ_QUESTION_SECONDS", 20)),
    joinAttemptsPerMinute: Setting("QUIZ_JOIN_ATTEMPTS_PER_MINUTE", 20));
var createAttemptsPerMinute = Setting("QUIZ_CREATE_ATTEMPTS_PER_MINUTE", 5);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IQuestionSetSource>(
    new JsonQuestionSetSource(Path.Combine(builder.Environment.ContentRootPath, "questions")));
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<IHostAuthorizer>(new KeyHostAuthorizer(hostKey));
builder.Services.AddSingleton(sp => new JoinThrottle(sp.GetRequiredService<TimeProvider>(), options.JoinAttemptsPerMinute));
builder.Services.AddKeyedSingleton(GameHub.CreateThrottleKey,
    (sp, _) => new JoinThrottle(sp.GetRequiredService<TimeProvider>(), createAttemptsPerMinute));
builder.Services.AddHostedService<IdleRoomSweeper>();

// Markers and phases travel as camelCase words ("hearts", "lobby"), matching the question files.
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok());
app.MapHub<GameHub>("/hub");

app.Run();

public partial class Program { }
