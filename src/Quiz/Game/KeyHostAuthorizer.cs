using System.Security.Cryptography;
using System.Text;

namespace Quiz.Game;

/// <summary>Host check against a single configured key. Fails closed when no key is configured.</summary>
public sealed class KeyHostAuthorizer(string? configuredKey) : IHostAuthorizer
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(configuredKey ?? "");

    public bool IsHost(string? key)
    {
        if (string.IsNullOrWhiteSpace(configuredKey)) return false; // FAIL-CLOSED INVARIANT: no key configured => nobody is host. Do not remove.
        if (key is null) return false; // an absent attempt is never the host (also guards GetBytes below)

        return CryptographicOperations.FixedTimeEquals(_key, Encoding.UTF8.GetBytes(key));
    }
}
