# Quiz

A small multiplayer quiz game for a living room: the TV shows the board, the host drives the round from a phone, and the players buzz in from theirs. It doubles as a worked example of a real-time app built on ASP.NET Core SignalR, and a hosted copy runs at https://quiz.kivancerturk.com.

## Question sets

Question sets are plain JSON files in `src/Quiz/questions/`. Each set has an `id`, a `title`, a `language`, and at least ten multiple-choice questions with four options each: a set is a pool, and a round draws ten of it at random. The options are marked `hearts`, `diamonds`, `clubs` and `spades` (never letters), and exactly one is `correct`. The shipped sets are in Turkish. `GET /sets` lists each set's `id`, `title` and `language` — never its questions — and that is where the host screen's picker comes from, so adding a set is adding a file.

## The screens

Three static pages, served by the app itself. `/tv` is the **television**: a display with no field
and no button on it, because the only thing in front of it is a remote control. It shows a
four-digit screen code until a phone claims it, and from then on the game — lobby, question,
reveal, and a podium for the first three at the end. `/` is the **phone**, the one page everybody
uses: a name, then four suit buzzers and nothing else. It is also where a room is opened. Whoever
holds the host key opens the room from that same form, takes a seat like everyone else, and gets
a short row of buttons under the buzzers — start, pause, next, another round, close. The host
plays; there is no second device to hold. `/host.html` is the older **console**, a page with the
host's buttons and no seat, for somebody who wants to run a round without playing in it. The
television no longer points at it.

Nobody has to type an address off a television, and nobody has to know who is supposed to scan
first. The television shows one QR code at a time, and both open the phone page: its own screen
code while it has no room on it, the room code once it has. A guest who scans before the room
exists types a name and waits there — the hub tells that phone the room the moment one opens on
that screen (`AwaitRoom`, then `RoomReady`), and it walks in by itself. A guest is never shown a
key field: opening the room is behind a line of small print, which a phone that already holds the
key finds open. The screen code is replaced every two minutes, so a phone that has waited at a
screen may claim whatever that screen is showing now; typing a key for the first time takes longer
than a code lives. The digits stay on screen for whoever would rather type, and the form takes
either code in the same field.

The QR encoder is `wwwroot/qr.js`, written for this game: byte mode, level L, versions 1 to 5.

A room can only be opened on a claimed screen, which is why opening one starts from the code on the
television, and the key is typed once per phone, which remembers it. The television paces the round: the countdown it shows is the one that closes the question, and
the server keeps no timers. A question closes three seconds after the last player has answered,
with the countdown as the backstop for a phone that never does, and a reveal gives way to the next
question nine seconds later — the host's button moves it on sooner. The pacing is on the
television and not on the console because a console is a phone: it gets locked, or its owner opens
another tab to play, and a browser stops the timers of a page nobody is looking at. The television
is the one screen a round cannot be played without, so a round with a host who is also a player,
or whose phone is in a pocket, carries on. The hub lets the screen a room is shown on say two
things about it, with no key — this question's time is up, this reveal has been read — and each
names the question it means, so a late timer moves nothing. A pause stops
the clock in the room itself, so the time spent waiting costs the players nothing and answers are
refused until it starts again, and closing a room ends it for everyone, frees its code and sends
the television back to a screen code. When a round ends the host is offered another one in the same
room, from any set: everyone keeps their seat, the scores start from nothing, the lobby is open to
latecomers again, and the room does not ask a question it has already asked until the set runs out.
The players' phones are kept awake while they are in a room; the console is not, since nothing
waits for it.

Everything comes back on its own. A phone that reloads or loses its connection re-sends its name
and is back in its seat with its score; the phone that opened the room is its host again in any
tab, because what makes it the host is remembered by the phone and not by the page. A television
that reloads has fallen out of the room and shows a fresh screen code, saying which room it was
in: the host scanning that code is all it takes to put it straight back, mid-question and all,
and the host's phone says so when the television has gone. Phones that lose the connection keep trying until it is back.
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
