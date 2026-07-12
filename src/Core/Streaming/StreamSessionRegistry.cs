using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Core.Security;

namespace Core.Streaming;

public sealed record ClientView(
    string Id,
    string ClientIp,
    DateTime ConnectedUtc,
    int Fps,
    int Quality,
    int Monitor,
    SessionRole Role,
    GuestAccessLevel? GuestAccessLevel,
    Guid? GuestInviteId);

/// <summary>
/// Tracks active stream sessions so the host (tray app) and the /api/clients
/// endpoints can list connected clients and forcibly disconnect them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StreamSessionRegistry
{
    private readonly ConcurrentDictionary<string, StreamSession> m_Sessions = new();
    private readonly ConcurrentDictionary<string, StreamSession> m_ControlTokens = new();

    public event Action<ClientView>? ClientJoined;

    public int Count => m_Sessions.Count;

    public void Add(StreamSession session)
    {
        m_Sessions[session.Id] = session;
        m_ControlTokens[session.ControlToken] = session;
        ClientJoined?.Invoke(ToView(session));
    }

    public void Remove(string id)
    {
        if (m_Sessions.TryRemove(id, out var session))
            m_ControlTokens.TryRemove(session.ControlToken, out _);
    }

    public bool TryGetByControlToken(string token, out StreamSession session) =>
        m_ControlTokens.TryGetValue(token, out session!);

    public IReadOnlyList<ClientView> Snapshot() => m_Sessions.Values
        .Select(ToView)
        .ToList();

    private static ClientView ToView(StreamSession session) => new(
        session.Id, session.ClientIp, session.ConnectedUtc, session.Fps, session.Quality, session.Monitor,
        session.Role, session.GuestAccessLevel, session.GuestInviteId);

    public bool Disconnect(string id)
    {
        if (m_Sessions.TryGetValue(id, out var session))
        {
            session.Kick();
            return true;
        }
        return false;
    }

    public void DisconnectAll()
    {
        foreach (var session in m_Sessions.Values)
            session.Kick();
    }

    public int CountForGuestInvite(Guid inviteId) =>
        m_Sessions.Values.Count(session => session.GuestInviteId == inviteId);

    public void DisconnectGuestInvite(Guid inviteId)
    {
        foreach (var session in m_Sessions.Values.Where(session => session.GuestInviteId == inviteId))
            session.Kick();
    }
}
