# Quiz

A small multiplayer quiz game for a living room: one screen (a TV) shows the board and the host drives the round, while players buzz in from their phones. It doubles as a worked example of a real-time app built on ASP.NET Core SignalR, and a hosted copy runs at https://quiz.kivancerturk.com.

## Question sets

Question sets are plain JSON files in `src/Quiz/questions/`. Each set has an `id`, a `title`, a `language`, and exactly ten multiple-choice questions with four options each. The options are marked `hearts`, `diamonds`, `clubs` and `spades` (never letters), and exactly one is `correct`. The shipped sets are in Turkish.

## The screens

Two static pages, served by the app itself: `/` is the phone (room code and name, then four
suit buzzers and nothing else) and `/host.html` is the TV (lobby, question, reveal, standings).
The host drives the round and owns the countdown; the server keeps no timers. Both pages are
plain HTML, CSS and ES modules with a TR / EN switch — no build step, no npm, no framework.

The SignalR browser client is vendored rather than loaded from a CDN:
`src/Quiz/wwwroot/lib/signalr/signalr.min.js` is `dist/browser/signalr.min.js` taken from
**@microsoft/signalr 10.0.11** on npm (tarball sha-512
`FulOJ2EEtKvLQcswe/U7v8pzyXyk7Jua5xgWJPPwyQU/2Z9ORvKcVjyO75VvLsem+CJ1ORRexJ+7Bz1FCei2aw==`).
To move to another version, download that version's tarball and copy the one file out of it.
No web fonts are shipped: the pages ask for `Geist Mono` and fall back to the system monospace.

## Development

```bash
dotnet test

# The app refuses to start without a host key of at least 16 characters.
QUIZ_HOST_KEY=dev-key-at-least-16 dotnet run --project src/Quiz
```

## Licence

MIT, see `LICENSE`.
