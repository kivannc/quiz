using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using System.Text.Json.Serialization;
using Quiz.Game;
using Quiz.Hosting;
using Quiz.Hubs;
using Quiz.Pairing;

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

// Standing in front of a television is counted apart from joining a room, at the same rate: a
// phone that scans a television does one and then the other, and a household shares one address.
var awaitThrottle = new JoinThrottle(TimeProvider.System, options.JoinAttemptsPerMinute);

var claimAttemptsPerMinute = Setting("QUIZ_CLAIM_ATTEMPTS_PER_MINUTE", 10);
var claimThrottle = new JoinThrottle(TimeProvider.System, claimAttemptsPerMinute);
// Constructed here for the same reason: PairingOptions validates itself, so a bad value stops startup.
var pairingOptions = new PairingOptions(
    lifetime: TimeSpan.FromSeconds(Setting("QUIZ_PAIRING_SECONDS", 120)),
    issuesPerMinute: Setting("QUIZ_PAIRING_ISSUES_PER_MINUTE", 10));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IQuestionSetSource>(
    new JsonQuestionSetSource(Path.Combine(builder.Environment.ContentRootPath, "questions")));
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<ConnectionRooms>();
builder.Services.AddSingleton<IHostAuthorizer>(new KeyHostAuthorizer(hostKey));
builder.Services.AddSingleton(sp => new JoinThrottle(sp.GetRequiredService<TimeProvider>(), options.JoinAttemptsPerMinute));
builder.Services.AddKeyedSingleton(GameHub.CreateThrottleKey, createThrottle);
builder.Services.AddKeyedSingleton(GameHub.ClaimThrottleKey, claimThrottle);
builder.Services.AddKeyedSingleton(GameHub.AwaitThrottleKey, awaitThrottle);
builder.Services.AddSingleton(pairingOptions);
builder.Services.AddSingleton<PairingStore>();
builder.Services.AddHostedService<IdleRoomSweeper>();

// Markers and phases travel as camelCase words ("hearts", "lobby"), matching the question files.
// A phone's connection dies without a word all the time: the screen locks, the radio sleeps, the
// network changes from Wi-Fi to mobile. Nothing closes; the page goes on believing it is connected
// and shows the last thing it was told, while the room has moved on. The only thing that tells a
// client so is the server's silence, and by default it takes thirty seconds of it — three questions'
// worth — to be believed. A ping every five seconds lets the pages give up after twelve.
builder.Services.AddSignalR(o => o.KeepAliveInterval = TimeSpan.FromSeconds(5)).AddJsonProtocol(o =>
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
// The pages are a handful of files that depend on each other and are replaced together by a
// release: a page kept from last week beside a script fetched today is a script looking for a
// button that is not there. "no-cache" does not mean "do not keep it" — it means ask first, and the
// answer is nearly always a 304 with no body — so every phone gets a set of files that belong together.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapGet("/healthz", () => Results.Ok());
// The host screen offers a choice of set, so it has to know what there is. No key is asked for:
// a set's id, title and language are not secret, and the questions themselves are not sent.
app.MapGet("/sets", (IQuestionSetSource sets) =>
    Results.Ok(sets.All().Select(s => new { s.Id, s.Title, s.Language })));
// The television's address is typed in with a remote, so the screen answers at "/tv" as well as at
// "/tv.html". It is the one address anybody enters on that device, and ".html" is four keystrokes
// too many on a remote control.
app.MapGet("/tv", (HttpContext http, IWebHostEnvironment env) =>
{
    http.Response.Headers.CacheControl = "no-cache";
    return Results.File(Path.Combine(env.WebRootPath, "tv.html"), "text/html");
});
// The host used to have a page of their own here, a console with buttons and no seat. Hosting is
// done from the page everybody uses now, so a phone that still has the old address — a bookmark, a
// browser that offers it from history — is sent there instead of to a page that is not found.
app.MapGet("/host.html", () => Results.Redirect("/"));
app.MapHub<GameHub>("/hub");

app.Run();

public partial class Program { }
