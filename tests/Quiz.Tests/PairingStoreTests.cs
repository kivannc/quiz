using Microsoft.Extensions.Time.Testing;
using Quiz.Pairing;

namespace Quiz.Tests;

public sealed class PairingStoreTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    private PairingStore NewStore(int issuesPerMinute = 1000) =>
        new(_clock, new PairingOptions(Lifetime, issuesPerMinute));

    private static string Code(string? code) => Assert.IsType<string>(code);

    [Fact]
    public void Issue_returns_a_four_digit_code()
    {
        var code = Code(NewStore().Issue("screen-1", "client-1"));

        Assert.Equal(4, code.Length);
        Assert.InRange(int.Parse(code), 1000, 9999);
    }

    [Fact]
    public void Codes_are_unique_across_live_pairings()
    {
        var store = NewStore();

        var codes = Enumerable.Range(0, 300)
            .Select(i => Code(store.Issue($"screen-{i}", $"client-{i}")))
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void Claim_returns_the_screen_that_issued_the_code()
    {
        var store = NewStore();
        var code = Code(store.Issue("screen-1", "client-1"));

        var result = store.Claim(code);

        Assert.Equal("screen-1", Assert.IsType<ClaimResult.Claimed>(result).ScreenConnectionId);
    }

    [Fact]
    public void A_code_can_only_be_claimed_once()
    {
        var store = NewStore();
        var code = Code(store.Issue("screen-1", "client-1"));

        Assert.IsType<ClaimResult.Claimed>(store.Claim(code));
        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim(code));
    }

    [Fact]
    public void A_code_can_only_be_claimed_once_under_concurrent_claims()
    {
        var store = NewStore();
        var code = Code(store.Issue("screen-1", "client-1"));

        var results = new ClaimResult[32];
        Parallel.For(0, results.Length, i => results[i] = store.Claim(code));

        Assert.Equal(1, results.OfType<ClaimResult.Claimed>().Count());
    }

    [Fact]
    public void An_expired_code_cannot_be_claimed()
    {
        var store = NewStore();
        var code = Code(store.Issue("screen-1", "client-1"));

        _clock.Advance(Lifetime);

        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim(code));
    }

    [Fact]
    public void A_code_is_still_claimable_just_before_its_lifetime_ends()
    {
        var store = NewStore();
        var code = Code(store.Issue("screen-1", "client-1"));

        _clock.Advance(Lifetime - TimeSpan.FromMilliseconds(1));

        Assert.IsType<ClaimResult.Claimed>(store.Claim(code));
    }

    [Fact]
    public void SweepExpired_drops_codes_past_their_lifetime_and_frees_them()
    {
        var store = NewStore();
        var old = Code(store.Issue("screen-old", "client-1"));
        _clock.Advance(Lifetime - TimeSpan.FromMinutes(1));
        var fresh = Code(store.Issue("screen-fresh", "client-2"));
        _clock.Advance(TimeSpan.FromMinutes(1));

        var swept = store.SweepExpired();

        Assert.Equal(1, swept);
        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim(old));
        Assert.IsType<ClaimResult.Claimed>(store.Claim(fresh));
        Assert.Equal(0, store.SweepExpired());
    }

    [Fact]
    public void Swept_codes_become_drawable_again()
    {
        // Crowd the 1000..9999 space until a bounded draw gives up, then sweep: issuing must succeed again.
        var store = NewStore();
        var issued = 0;
        while (store.Issue($"screen-{issued}", $"client-{issued}") is not null) issued++;
        Assert.True(issued > 5000, $"expected to crowd the space, issued {issued}");

        _clock.Advance(Lifetime);
        Assert.Equal(issued, store.SweepExpired());

        Assert.NotNull(store.Issue("late-screen", "late-client"));
    }

    [Fact]
    public void Claim_of_an_unknown_an_expired_and_a_claimed_code_are_indistinguishable()
    {
        var store = NewStore();
        var expired = Code(store.Issue("screen-1", "client-1"));
        _clock.Advance(Lifetime);
        var claimed = Code(store.Issue("screen-2", "client-2"));
        store.Claim(claimed);

        var results = new[] { store.Claim("0000"), store.Claim(expired), store.Claim(claimed) };

        Assert.All(results, r => Assert.Equal(new ClaimResult.NoSuchCode(), r));
        Assert.All(results, r => Assert.Same(typeof(ClaimResult.NoSuchCode), r.GetType()));
    }

    [Fact]
    public void Claim_of_a_malformed_code_is_no_such_code()
    {
        var store = NewStore();

        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim(null!));
        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim(""));
        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim("abcd"));
    }

    [Fact]
    public void Issue_is_throttled_per_client_key()
    {
        var store = NewStore(issuesPerMinute: 3);
        for (var i = 0; i < 3; i++) Assert.NotNull(store.Issue($"screen-{i}", "client-1"));

        Assert.Null(store.Issue("screen-3", "client-1"));
        Assert.NotNull(store.Issue("screen-4", "client-2"));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(store.Issue("screen-5", "client-1"));
    }

    [Fact]
    public void A_throttled_issue_leaves_the_screens_existing_code_alone()
    {
        var store = NewStore(issuesPerMinute: 1);
        var code = Code(store.Issue("screen-1", "client-1"));

        Assert.Null(store.Issue("screen-1", "client-1"));

        Assert.IsType<ClaimResult.Claimed>(store.Claim(code));
    }

    [Fact]
    public void Forget_removes_the_code_a_screen_was_holding()
    {
        var store = NewStore();
        var code = Code(store.Issue("screen-1", "client-1"));
        var other = Code(store.Issue("screen-2", "client-2"));

        store.Forget("screen-1");

        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim(code));
        Assert.IsType<ClaimResult.Claimed>(store.Claim(other));
    }

    [Fact]
    public void Forget_of_an_unknown_screen_does_nothing()
    {
        var store = NewStore();
        var code = Code(store.Issue("screen-1", "client-1"));

        store.Forget("nobody");

        Assert.IsType<ClaimResult.Claimed>(store.Claim(code));
    }

    [Fact]
    public void A_screen_that_issues_twice_keeps_only_its_newest_code()
    {
        var store = NewStore();
        var first = Code(store.Issue("screen-1", "client-1"));
        var second = Code(store.Issue("screen-1", "client-1"));

        Assert.NotEqual(first, second);
        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim(first));
        Assert.Equal("screen-1", Assert.IsType<ClaimResult.Claimed>(store.Claim(second)).ScreenConnectionId);
    }

    [Fact]
    public void Options_refuse_a_non_positive_lifetime_and_fewer_than_one_issue_per_minute()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PairingOptions(TimeSpan.Zero, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PairingOptions(TimeSpan.FromSeconds(-1), 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PairingOptions(Lifetime, 0));
        _ = new PairingOptions(Lifetime, 1);
    }
}
