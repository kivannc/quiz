using System.Security.Cryptography;
using System.Text;

namespace Quiz.Game;

/// <summary>Host check against a single configured key. Fails closed when no key is configured.</summary>
public sealed class KeyHostAuthorizer(string? configuredKey) : IHostAuthorizer
{
    private readonly byte[]? _key = string.IsNullOrWhiteSpace(configuredKey) ? null : Encoding.UTF8.GetBytes(configuredKey);

    public bool IsHost(string? key)
    {
        if (_key is null) return false; // fail closed: no key configured means nobody is the host

        if (string.IsNullOrEmpty(key)) return false;
        return CryptographicOperations.FixedTimeEquals(_key, Encoding.UTF8.GetBytes(key));
    }
}
