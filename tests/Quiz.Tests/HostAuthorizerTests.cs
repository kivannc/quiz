using Quiz.Game;

namespace Quiz.Tests;

public sealed class HostAuthorizerTests
{
    [Fact]
    public void IsHost_is_true_for_the_configured_key()
    {
        Assert.True(new KeyHostAuthorizer("s3cret-key").IsHost("s3cret-key"));
    }

    [Theory]
    [InlineData("s3cret-keY")]
    [InlineData("s3cret-ke")]
    [InlineData("s3cret-key ")]
    public void IsHost_is_false_for_a_wrong_key(string attempt)
    {
        Assert.False(new KeyHostAuthorizer("s3cret-key").IsHost(attempt));
    }

    // Fail closed: with no key configured nobody is the host, including an attempt that
    // "matches" the unconfigured value. Each attempt is a case that would pass a naive equality check.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsHost_is_false_for_every_key_when_none_is_configured(string? configured)
    {
        var authorizer = new KeyHostAuthorizer(configured);

        Assert.False(authorizer.IsHost(configured));
        Assert.False(authorizer.IsHost(""));
        Assert.False(authorizer.IsHost("   "));
        Assert.False(authorizer.IsHost("anything"));
        Assert.False(authorizer.IsHost(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsHost_is_false_for_a_null_or_empty_attempt(string? attempt)
    {
        Assert.False(new KeyHostAuthorizer("s3cret-key").IsHost(attempt));
    }
}
