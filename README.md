# Quiz

A small multiplayer quiz game for a living room: the TV shows the board, the host drives the round from a phone, and the players buzz in from theirs. It doubles as a worked example of a real-time app built on ASP.NET Core SignalR, and a hosted copy runs at https://quiz.kivancerturk.com.

## Question sets

Question sets are plain JSON files in `src/Quiz/questions/`. Each set has an `id`, a `title`, a `language`, and at least ten multiple-choice questions with four options each: a set is a pool, and a round draws ten of it at random. The options are marked `hearts`, `diamonds`, `clubs` and `spades` (never letters), and exactly one is `correct`. The shipped sets are in Turkish. `GET /sets` lists each set's `id`, `title` and `language` — never its questions — and that is where the host screen's picker comes from, so adding a set is adding a file.

## The screens

Three static pages, served by the app itself. `/tv` is the **television**: a display with no field
and no button on it, because the only thing in front of it is a remote control. It shows a
four-digit screen code until a phone claims it, and from then on the game — lobby, question,
reveal, and a podium for the first three at the end. `/host.html` is the **console**: the host's phone, laid out for one hand. It
claims the television with the code on screen, opens the room on it, and then carries the pacing
and the buttons — start, pause, next, close — and none of the game, because the host is looking at
the television like everyone else. `/` is the **player's phone**: room code and name, then four
suit buzzers and nothing else.

Nobody has to type an address off a television. The screen code and the room code each come with
a QR code: scanning the first opens the console with the code already filled in, scanning the
second opens the join form the same way. The digits stay on screen for whoever would rather type.
The QR encoder is `wwwroot/qr.js`, written for this game: byte mode, level L, versions 1 to 5.

A room can only be opened on a claimed screen, which is why the console asks for the screen code
first, and the key is only ever typed into the console — once per phone, which remembers it. The console drives the round and owns the
countdown; the server keeps no timers. A question closes three seconds after the last player has
answered, with the countdown as the backstop for a phone that never does, and a reveal gives way to
the next question nine seconds later — the button on the console moves it on sooner. A pause stops
the clock in the room itself, so the time spent waiting costs the players nothing and answers are
refused until it starts again, and closing a room ends it for everyone, frees its code and sends
the television back to a screen code. When a round ends the console offers another one in the same
room, from any set: everyone keeps their seat, the scores start from nothing, the lobby is open to
latecomers again, and the room does not ask a question it has already asked until the set runs out.
Because the pacing lives on the console, that phone is kept
awake for as long as a room is open; so are the players' phones while they are in one.

Both halves come back on their own. A console reloaded mid-round re-sends its key, asks the hub for
the room as it stands and paints itself back with the countdown it was already running, paused if
that is how it left it; it needs no screen code to do so, because the television is still in the
room. A television that reloads has fallen out of it and shows a fresh screen code, saying which
room it was in: typing that code into the console, or scanning it, puts it straight back,
mid-question and all. Phones that lose the connection keep trying until it is back.
All three pages are plain HTML, CSS and ES modules with a TR / EN switch — no build step, no npm,
no framework.

The SignalR browser client is vendored rather than loaded from a CDN:
`src/Quiz/wwwroot/lib/signalr/signalr.min.js` is `dist/browser/signalr.min.js` taken from
**@microsoft/signalr 10.0.11** on npm (tarball sha-512
`FulOJ2EEtKvLQcswe/U7v8pzyXyk7Jua5xgWJPPwyQU/2Z9ORvKcVjyO75VvLsem+CJ1ORRexJ+7Bz1FCei2aw==`).
To move to another version, download that version's tarball and copy the one file out of it. The
only edit to the file is that its trailing `sourceMappingURL` comment is removed, because the `.map`
it names is not shipped and every load with devtools open would ask for it.
No web fonts are shipped: the pages ask for `Geist Mono` and fall back to the system monospace.

## Development

```bash
dotnet test

# The app refuses to start without a host key of at least 16 characters.
QUIZ_HOST_KEY=dev-key-at-least-16 dotnet run --project src/Quiz
```

## Licence

MIT, see `LICENSE`.
