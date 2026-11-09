using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
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
// Built here, not lazily in DI, so a bad value stops startup like every other limit.
var createThrottle = new JoinThrottle(TimeProvider.System, createAttemptsPerMinute);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IQuestionSetSource>(
    new JsonQuestionSetSource(Path.Combine(builder.Environment.ContentRootPath, "questions")));
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<ConnectionRooms>();
builder.Services.AddSingleton<IHostAuthorizer>(new KeyHostAuthorizer(hostKey));
builder.Services.AddSingleton(sp => new JoinThrottle(sp.GetRequiredService<TimeProvider>(), options.JoinAttemptsPerMinute));
builder.Services.AddKeyedSingleton(GameHub.CreateThrottleKey, createThrottle);
builder.Services.AddHostedService<IdleRoomSweeper>();

// Markers and phases travel as camelCase words ("hearts", "lobby"), matching the question files.
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    // Trusting X-Forwarded-For from any sender is safe here only because this container publishes no ports:
    // the reverse proxy is the only thing that can reach it, which the deployment design guarantees, and it
    // sets the header itself. Without this every player would share the proxy's address and one throttle bucket.
    // Exactly one proxy hop: the middleware reads the header right to left, so with a limit of 1 only the entry the
    // proxy appended is used and a client cannot pick its own address. Raise this only if a second hop is added.
    o.ForwardLimit = 1;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok());
// The host screen offers a choice of set, so it has to know what there is. No key is asked for:
// a set's id, title and language are not secret, and the questions themselves are not sent.
app.MapGet("/sets", (IQuestionSetSource sets) =>
    Results.Ok(sets.All().Select(s => new { s.Id, s.Title, s.Language })));
app.MapHub<GameHub>("/hub");

app.Run();

public partial class Program { }
