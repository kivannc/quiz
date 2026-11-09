namespace Quiz.Game;

/// <summary>Decides who may open a room. Only the host key holder can.</summary>
public interface IHostAuthorizer
{
    bool IsHost(string? key);
}
