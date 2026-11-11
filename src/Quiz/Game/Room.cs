using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Quiz.Game;

public enum RoomPhase { Lobby, Question, Reveal, Finished }

public enum JoinResult { Joined, Rejoined, RoomFull, RoundInProgress }

public sealed record PlayerView(string Name, int Score);

/// <summary>
/// One question as the screens are sent it. <paramref name="Round"/> counts the rounds a room has
/// played, from one: a room can play several, each numbering its questions from zero again, so the
/// index alone no longer says which question this is — and a phone that remembers "I answered
/// question 0" must not take that for an answer to the first question of the round after.
/// </summary>
public sealed record QuestionView(int Index, int Total, string Text, IReadOnlyList<Option> Options, int LimitSeconds, int Round);

/// <summary>
/// What a closed question looks like to every screen. <paramref name="Index"/> is the question it
/// closes, counted the way <see cref="QuestionView"/> counts: a reveal carries totals rather than
/// the points just earned, so a phone works out what it earned by subtracting the total it had
/// before that question — and the index, together with the <paramref name="Round"/> it was asked
/// in, is the only thing that says which question that was.
/// </summary>
public sealed record RevealView(
    int Index,
    Marker Correct,
    IReadOnlyDictionary<Marker, int> Distribution,
    IReadOnlyList<PlayerView> Standings,
    int Round);

/// <summary>
/// One room: its players and the rounds they play, one after another, each a run of questions in order.
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
    // The set the next draw is made from: the one the room was opened with, until a new round names another.
    private QuestionSet _set;
    // Every question this room has asked, as "set/question", so a second round asks something else.
    // It lives and dies with the room: nothing is remembered from one evening to the next.
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    // Which round this is, from one. Counted by Start, so a room in its first lobby is in round zero.
    private int _round;
    // The questions this round plays: drawn from the set by Start, empty until then.
    private IReadOnlyList<Question> _questions = [];
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

    /// <summary>
    /// How many questions one round plays. A set is a pool rather than a round: a category holds far more
    /// than this, and <see cref="Start"/> draws this many from it, so the same category played twice is not
    /// the same questions. A pool smaller than a round plays whole.
    /// </summary>
    public const int QuestionsPerRound = 10;

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
                var q = _questions[_questionIndex];
                return new QuestionView(_questionIndex, _questions.Count, q.Text, q.Options, (int)_questionLimit.TotalSeconds, _round);
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
    public JoinResult Join(string name, string connectionId) => Join(name, connectionId, out _);

    /// <summary>
    /// As <see cref="Join(string, string)"/>, and says which connections the seat was taken from on a
    /// rejoin. A seat belongs to one connection at a time, so a second tab or a second phone under the
    /// same name leaves the first with buzzers the room refuses; whoever calls this can tell that
    /// first one so, instead of leaving it to look broken.
    /// </summary>
    public JoinResult Join(string name, string connectionId, out IReadOnlyList<string> displaced)
    {
        displaced = [];
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(connectionId);
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A name must not be empty.", nameof(name));

        lock (_gate)
        {
            if (_seatByName.TryGetValue(name, out var existing))
            {
                var stale = _seatByConnection.Where(kv => kv.Value == existing).Select(kv => kv.Key).ToList();
                foreach (var gone in stale) _seatByConnection.Remove(gone);
                displaced = stale.Where(id => id != connectionId).ToList();
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

    /// <summary>
    /// Opens the first question. Refused outside the lobby or with nobody seated.
    /// Draws the round's questions, so every round a room plays is a different draw.
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_phase != RoomPhase.Lobby || _seats.Count == 0) return false;

            _questions = Draw();
            _round++;
            _questionIndex = 0;
            OpenQuestion();
            Touch();
            return true;
        }
    }

    /// <summary>
    /// Takes a finished room back to its lobby for another round, played from <paramref name="set"/>.
    /// Everyone keeps their seat and their connection, so nobody joins again; the scores go back to
    /// nothing, because a round ends on a podium and the next one has its own. Being a lobby again,
    /// the room takes new players until the round starts — which is how somebody who arrived halfway
    /// through the last one gets in. Refused (false) unless the round before has finished.
    /// </summary>
    public bool NewRound(QuestionSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Questions.Count == 0) throw new ArgumentException("A round needs at least one question.", nameof(set));

        lock (_gate)
        {
            if (_phase != RoomPhase.Finished) return false;

            _set = set;
            foreach (var seat in _seats) seat.Score = 0;
            _answers.Clear();
            _reveal = null;
            _questions = [];
            _questionIndex = 0;
            _pausedAt = null;
            _pausedFor = TimeSpan.Zero;
            _phase = RoomPhase.Lobby;
            Touch();
            return true;
        }
    }

    /// <summary>
    /// A round's questions: <see cref="QuestionsPerRound"/> of the set, in a random order, each at most
    /// once. A set smaller than a round plays whole, which is what a one-question test set is.
    /// Copies the set before shuffling, so rooms can share one.
    ///
    /// A room that plays several rounds does not ask a question twice while the set still has one it
    /// has not asked: questions this room has not seen are drawn first, and only when they run out is
    /// the round filled up from the ones it has. A set that has run out starts over from there, so
    /// the round after is fresh again rather than made of leftovers. All of that is this room's own
    /// memory; a new room knows nothing of it.
    ///
    /// The same generator the room codes are drawn with, for no better reason than that a game has one
    /// source of chance; a question draw asks nothing of it that a shuffle could not give.
    /// </summary>
    private IReadOnlyList<Question> Draw()
    {
        var pool = _set.Questions.ToArray();
        RandomNumberGenerator.Shuffle(pool.AsSpan());
        var count = Math.Min(QuestionsPerRound, pool.Length);

        // Stable, so each half keeps the shuffle it was given: the unasked ones, then the rest.
        var drawn = pool.OrderBy(q => _asked.Contains(Key(q)) ? 1 : 0).Take(count).ToArray();
        if (drawn.Any(q => _asked.Contains(Key(q))))
        {
            // The set has run out. What this round repeats is forgotten along with everything else
            // asked from this set, and the round itself is remembered below like any other.
            _asked.RemoveWhere(key => key.StartsWith(_set.Id + "/", StringComparison.Ordinal));
            // Without this the repeats would always be the round's last questions.
            RandomNumberGenerator.Shuffle(drawn.AsSpan());
        }
        foreach (var q in drawn) _asked.Add(Key(q));
        return new ReadOnlyCollection<Question>(drawn);
    }

    // Question ids are only unique inside their own set.
    private string Key(Question question) => _set.Id + "/" + question.Id;

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

            var correct = marker == _questions[_questionIndex].Correct;
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
                _questionIndex,
                _questions[_questionIndex].Correct,
                new ReadOnlyDictionary<Marker, int>(distribution),
                Standings, // the lock is reentrant; Standings owns the ordering rule
                _round);
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
            if (_questionIndex + 1 >= _questions.Count)
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
