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
    private RevealView? _reveal;
    private RoomPhase _phase = RoomPhase.Lobby;
    private DateTimeOffset _lastActivity;

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

    /// <summary>The last time an accepted call changed the room. Refused calls do not count.</summary>
    public DateTimeOffset LastActivity { get { lock (_gate) return _lastActivity; } }

    public IReadOnlyList<PlayerView> Players { get { lock (_gate) return _seats.Select(s => new PlayerView(s.Name, s.Score)).ToList(); } }

    /// <summary>Players best score first; equal scores stay in join order (the same ordering <see cref="CloseQuestion"/> reports).</summary>
    public IReadOnlyList<PlayerView> Standings
    {
        get { lock (_gate) return _seats.OrderByDescending(s => s.Score).Select(s => new PlayerView(s.Name, s.Score)).ToList(); }
    }

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

            var elapsed = _clock.GetUtcNow() - _questionStartedAt;
            if (elapsed > _questionLimit) return false;

            var correct = marker == _set.Questions[_questionIndex].Correct;
            _answers[seat] = new GivenAnswer(marker, Scoring.Points(correct, elapsed, _questionLimit));
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

            // OrderByDescending is stable, so equal scores stay in join order.
            var standings = _seats.OrderByDescending(s => s.Score).Select(s => new PlayerView(s.Name, s.Score)).ToList();

            _reveal = new RevealView(
                _set.Questions[_questionIndex].Correct,
                new ReadOnlyDictionary<Marker, int>(distribution),
                standings.AsReadOnly());
            _phase = RoomPhase.Reveal;
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
        _phase = RoomPhase.Question;
    }

    private void Touch() => _lastActivity = _clock.GetUtcNow();
}
