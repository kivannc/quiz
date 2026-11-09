# Quiz

A small multiplayer quiz game for a living room: one screen (a TV) shows the board and the host drives the round, while players buzz in from their phones. It doubles as a worked example of a real-time app built on ASP.NET Core SignalR, and a hosted copy runs at https://quiz.kivancerturk.com.

## Question sets

Question sets are plain JSON files in `src/Quiz/questions/`. Each set has an `id`, a `title`, a `language`, and exactly ten multiple-choice questions with four options each. The options are marked `hearts`, `diamonds`, `clubs` and `spades` (never letters), and exactly one is `correct`. The shipped sets are in Turkish.

## Development

```bash
dotnet test
```

## Licence

MIT, see `LICENSE`.
