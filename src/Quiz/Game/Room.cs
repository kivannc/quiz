using System.Collections.ObjectModel;

namespace Quiz.Game;

public enum RoomPhase { Lobby, Question, Reveal, Finished }

public enum JoinResult { Joined, Rejoined, RoomFull, RoundInProgress }

public sealed record PlayerView(string Name, int Score);

public sealed record QuestionView(int Index, int Total, string Text, IReadOnlyList<Option> Options, int LimitSeconds);

public sealed record RevealView(
    Marker Correct,
    IReadOnlyDictionary<Marker, int> Distribution,
    IReadOnlyList<PlayerView> Standings);

/// <summary>
/// One room: its players and one round of questions played in order.
/// There are no timers here. The host's page owns the countdown and calls <see cref="CloseQuestion"/>;
/// the room independently records when each question opened and refuses answers that arrive after the limit.
/// A pause is state, not a scheduler: the clock the room measures the limit by simply leaves out the time
/// it spent paused, so freezing the screen and freezing the room say the same thing.
/// Safe for concurrent use: every public member takes one internal lock, so simultaneous calls
/// (two phones answering at the same instant) are applied one at a time.
/// </summary>
public sealed class Room
{
    private sealed class Seat(string name)
    {
        public string Name { get; } = name;
        public int Score { get; set; }
    }

    private sealed record GivenAnswer(Marker Marker, int Points);

    private readonly object _gate = new();
    private readonly QuestionSet _set;
    private readonly TimeProvider _clock;
    private readonly int _maxPlayers;
    private readonly TimeSpan _questionLimit;

    // Insertion order is join order; it is also the tie-break for standings.
    private readonly List<Seat> _seats = [];
    private readonly Dictionary<string, Seat> _seatByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Seat> _seatByConnection = new(StringComparer.Ordinal);

    private readonly Dictionary<Seat, GivenAnswer> _answers = [];
    private int _questionIndex;
    private DateTimeOffset _questionStartedAt;
    // When the current pause began, null while the question is running, and how long this question has
    // been paused for in total. Both are reset by every question that opens.
    private DateTimeOffset? _pausedAt;
    private TimeSpan _pausedFor;
    private RevealView? _reveal;
    private RoomPhase _phase = RoomPhase.Lobby;
    private DateTimeOffset _lastActivity;

    /// <summary>
    /// How long a pause holds a room out of the idle sweep. Far longer than any walk away from the table
    /// a round survives, and short of forever: a paused screen that is simply abandoned would otherwise
    /// keep a room slot and its code for as long as the process lives, and there are only a few slots.
    /// Past this the room ages from the end of the grace like any other, and is reclaimed and told so.
    /// </summary>
    public static readonly TimeSpan PauseGrace = TimeSpan.FromHours(4);

    public Room(string code, QuestionSet set, TimeProvider clock, int maxPlayers, TimeSpan questionLimit)
    {
        if (set.Questions.Count == 0) throw new ArgumentException("A room needs at least one question.", nameof(set));

        Code = code;
        _set = set;
        _clock = clock;
        _maxPlayers = maxPlayers;
        _questionLimit = questionLimit;
        _lastActivity = clock.GetUtcNow();
    }

    public string Code { get; }

    public RoomPhase Phase { get { lock (_gate) return _phase; } }

    /// <summary>
    /// The last time an accepted call changed the room. Refused calls do not count. A paused room counts
    /// as active while it is paused: a long walk away from the table is what pausing is for, and the idle
    /// sweep must not take the room, the scores and the code while everyone is in the kitchen. That only
    /// holds for <see cref="PauseGrace"/>, after which the room is as old as the end of its grace and
    /// ages on from there, because a pause nobody ever came back to is an abandoned room.
    /// </summary>
    public DateTimeOffset LastActivity
    {
        get
        {
            lock (_gate)
            {
                if (_pausedAt is not { } since) return _lastActivity;
                var now = _clock.GetUtcNow();
                return now - since <= PauseGrace ? now : since + PauseGrace;
            }
        }
    }

    public IReadOnlyList<PlayerView> Players { get { lock (_gate) return _seats.Select(s => new PlayerView(s.Name, s.Score)).ToList(); } }

    /// <summary>Players best score first; equal scores stay in join order (the same ordering <see cref="CloseQuestion"/> reports).</summary>
    public IReadOnlyList<PlayerView> Standings
    {
        get { lock (_gate) return _seats.OrderByDescending(s => s.Score).Select(s => new PlayerView(s.Name, s.Score)).ToList().AsReadOnly(); }
    }

    /// <summary>Whether the open question's clock is stopped. False whenever no question is open.</summary>
    public bool Paused { get { lock (_gate) return _pausedAt is not null; } }

    /// <summary>
    /// How much of the open question's limit is left, or null when no question is open. Never
    /// negative: once the limit has passed this is zero, which is also when the room starts
    /// refusing answers. While the room is paused it stands still. Read-only, like
    /// <see cref="CurrentReveal"/>; a screen asking the time does not move it.
    /// </summary>
    public TimeSpan? Remaining
    {
        get
        {
            lock (_gate)
            {
                if (_phase != RoomPhase.Question) return null;
                var left = _questionLimit - Elapsed();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>The reveal being shown, or null outside the Reveal phase. Read-only: unlike <see cref="CloseQuestion"/> it never changes the room.</summary>
    public RevealView? CurrentReveal { get { lock (_gate) return _phase == RoomPhase.Reveal ? _reveal : null; } }

    public QuestionView? CurrentQuestion
    {
        get
        {
            lock (_gate)
            {
                if (_phase is not (RoomPhase.Question or RoomPhase.Reveal)) return null;
                var q = _set.Questions[_questionIndex];
                return new QuestionView(_questionIndex, _set.Questions.Count, q.Text, q.Options, (int)_questionLimit.TotalSeconds);
            }
        }
    }

    /// <summary>How many answers have been accepted for the current question.</summary>
    public int AnswerCount { get { lock (_gate) return _answers.Count; } }

    /// <summary>
    /// Seats <paramref name="name"/> (trimmed; matched case-insensitively; the spelling first seen is displayed;
    /// empty or whitespace throws). A name already in the room is a rejoin at any phase: the connection is
    /// remapped onto the existing seat and the score is kept. A new name needs a free seat and a lobby.
    /// </summary>
    public JoinResult Join(string name, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(connectionId);
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A name must not be empty.", nameof(name));

        lock (_gate)
        {
            if (_seatByName.TryGetValue(name, out var existing))
            {
                foreach (var stale in _seatByConnection.Where(kv => kv.Value == existing).Select(kv => kv.Key).ToList())
                {
                    _seatByConnection.Remove(stale);
                }
                _seatByConnection[connectionId] = existing;
                Touch();
                return JoinResult.Rejoined;
            }

            if (_phase != RoomPhase.Lobby) return JoinResult.RoundInProgress;
            if (_seats.Count >= _maxPlayers) return JoinResult.RoomFull;

            var seat = new Seat(name);
            _seats.Add(seat);
            _seatByName[name] = seat;
            _seatByConnection[connectionId] = seat;
            Touch();
            return JoinResult.Joined;
        }
    }

    /// <summary>Opens the first question. Refused outside the lobby or with nobody seated.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_phase != RoomPhase.Lobby || _seats.Count == 0) return false;

            _questionIndex = 0;
            OpenQuestion();
            Touch();
            return true;
        }
    }

    /// <summary>
    /// Records an answer. Refused (false) outside the Question phase, for an undefined marker, from an unknown connection, as a repeat
    /// from a seat that already answered, or after the limit has passed, whether or not the question was closed.
    /// </summary>
    public bool Answer(string connectionId, Marker marker)
    {
        lock (_gate)
        {
            if (_phase != RoomPhase.Question) return false;
            if (!Enum.IsDefined(marker)) return false;
            if (!_seatByConnection.TryGetValue(connectionId, out var seat)) return false;
            if (_answers.ContainsKey(seat)) return false;
            // A paused room is not asking anything: the clock is stopped, so there is no honest
            // moment to score this tap at, and the phone is told to lock its buzzers anyway.
            if (_pausedAt is not null) return false;

            var elapsed = Elapsed();
            if (elapsed > _questionLimit) return false;

            var correct = marker == _set.Questions[_questionIndex].Correct;
            _answers[seat] = new GivenAnswer(marker, Scoring.Points(correct, elapsed, _questionLimit));
            Touch();
            return true;
        }
    }

    /// <summary>
    /// Stops the question's clock: the limit stops running down, answers are refused, and the time spent
    /// paused never counts against the question. Refused (false) unless a question is open and running.
    /// </summary>
    public bool Pause()
    {
        lock (_gate)
        {
            if (_phase != RoomPhase.Question || _pausedAt is not null) return false;

            _pausedAt = _clock.GetUtcNow();
            Touch();
            return true;
        }
    }

    /// <summary>
    /// Starts the clock again with exactly the time that was left when it stopped. Refused (false) when the
    /// room is not paused, so resuming twice is harmless.
    /// </summary>
    public bool Resume()
    {
        lock (_gate)
        {
            if (_pausedAt is not { } since) return false;

            _pausedFor += _clock.GetUtcNow() - since;
            _pausedAt = null;
            Touch();
            return true;
        }
    }

    /// <summary>
    /// Ends the question: awards the points and reports the correct marker, how many picked each marker
    /// (all four, zeros included) and the standings. Calling it again during the reveal returns the same
    /// reveal; calling it in any other phase throws.
    /// </summary>
    public RevealView CloseQuestion()
    {
        lock (_gate)
        {
            if (_phase == RoomPhase.Reveal) return _reveal!;
            if (_phase != RoomPhase.Question) throw new InvalidOperationException($"No question is open (phase: {_phase}).");

            var distribution = Enum.GetValues<Marker>().ToDictionary(m => m, _ => 0);
            foreach (var answer in _answers.Values) distribution[answer.Marker]++;
            // Only mutate scores once everything that can throw has run.
            foreach (var (seat, answer) in _answers) seat.Score += answer.Points;


            _reveal = new RevealView(
                _set.Questions[_questionIndex].Correct,
                new ReadOnlyDictionary<Marker, int>(distribution),
                Standings); // the lock is reentrant; Standings owns the ordering rule
            _phase = RoomPhase.Reveal;
            // The clock this pause was holding is over, so the room is not paused any more: nothing
            // outside the Question phase measures time, and a reveal must not be painted as paused.
            _pausedAt = null;
            Touch();
            return _reveal;
        }
    }

    /// <summary>
    /// Moves on from the reveal: opens the next question (true) or finishes the round (false).
    /// Returns false when already finished; throws if no reveal is showing.
    /// </summary>
    public bool Advance()
    {
        lock (_gate)
        {
            if (_phase == RoomPhase.Finished) return false;
            if (_phase != RoomPhase.Reveal) throw new InvalidOperationException($"Nothing to advance from (phase: {_phase}).");

            Touch();
            if (_questionIndex + 1 >= _set.Questions.Count)
            {
                _phase = RoomPhase.Finished;
                return false;
            }

            _questionIndex++;
            OpenQuestion();
            return true;
        }
    }

    private void OpenQuestion()
    {
        _answers.Clear();
        _reveal = null;
        _questionStartedAt = _clock.GetUtcNow();
        _pausedAt = null;
        _pausedFor = TimeSpan.Zero;
        _phase = RoomPhase.Question;
    }

    /// <summary>
    /// How long the open question has actually been running: wall time since it opened, less every
    /// moment the room was paused. While paused it stands still at the instant the pause began, which
    /// is what makes a pause cost the players nothing. Call it under the lock.
    /// </summary>
    private TimeSpan Elapsed() => (_pausedAt ?? _clock.GetUtcNow()) - _questionStartedAt - _pausedFor;

    private void Touch() => _lastActivity = _clock.GetUtcNow();
}
