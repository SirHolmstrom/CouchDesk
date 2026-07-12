using Core.Security;

namespace Core.Streaming;

public sealed record PointerInputDecision(bool Allowed, string BlockedBy, int RetryAfterMs);
public sealed record PointerCursorState(string Source, int HostBlockMs);
public sealed record PointerControlView(string Source, string? RemoteOwnerId, int HostBlockMs, int RemoteIdleMs);

/// <summary>
/// Shared steering-wheel lock for mouse input across all connected sessions.
/// The host's physical mouse is always strongest; remote users keep the wheel
/// briefly while active so two clients cannot fight over the cursor.
/// </summary>
public sealed class PointerInputArbiter
{
    private const int HostLockMs = 3500;
    private const int HigherPriorityTakeoverIdleMs = 1000;
    private const int SamePriorityTakeoverIdleMs = 1500;
    private const int LowerPriorityTakeoverIdleMs = 2000;
    private const int RemoteEchoHistoryMs = 2000;
    private const int RemoteEchoTolerancePx = 32;
    private const int HostMoveDeadzonePx = 2;
    private const int MaxRemoteEchoPoints = 96;

    private readonly object m_Gate = new();
    private readonly List<RemoteEchoPoint> m_RemoteEchoTrail = new();

    private string? m_RemoteOwnerId;
    private int m_RemoteOwnerPriority;
    private long m_RemoteOwnerLastInputMs;
    private long m_HostLockUntilMs;

    private int? m_LastObservedX;
    private int? m_LastObservedY;
    public PointerInputDecision TryBeginRemoteInput(string sessionId, int priority)
    {
        lock (m_Gate)
        {
            long now = NowMs();
            if (now < m_HostLockUntilMs)
                return new PointerInputDecision(false, "host", ClampRetry(m_HostLockUntilMs - now));

            if (m_RemoteOwnerId is null || m_RemoteOwnerId == sessionId)
            {
                SetRemoteOwner(sessionId, priority, now);
                return new PointerInputDecision(true, "", 0);
            }

            long idleMs = now - m_RemoteOwnerLastInputMs;
            int requiredIdleMs = priority > m_RemoteOwnerPriority
                ? HigherPriorityTakeoverIdleMs
                : priority == m_RemoteOwnerPriority
                    ? SamePriorityTakeoverIdleMs
                    : LowerPriorityTakeoverIdleMs;

            if (idleMs >= requiredIdleMs)
            {
                SetRemoteOwner(sessionId, priority, now);
                return new PointerInputDecision(true, "", 0);
            }

            return new PointerInputDecision(false, "remote", requiredIdleMs - (int)Math.Max(0, idleMs));
        }
    }

    public void NoteRemoteMove(int x, int y)
    {
        lock (m_Gate)
        {
            long now = NowMs();
            PurgeRemoteEchoTrail(now);
            m_RemoteEchoTrail.Add(new RemoteEchoPoint(now, x, y));
            while (m_RemoteEchoTrail.Count > MaxRemoteEchoPoints)
                m_RemoteEchoTrail.RemoveAt(0);
        }
    }

    public void ObserveCursor(int x, int y)
    {
        lock (m_Gate)
        {
            long now = NowMs();
            if (m_LastObservedX is null || m_LastObservedY is null)
            {
                m_LastObservedX = x;
                m_LastObservedY = y;
                return;
            }

            if (DistanceSquared(x, y, m_LastObservedX.Value, m_LastObservedY.Value)
                <= HostMoveDeadzonePx * HostMoveDeadzonePx)
                return;

            PurgeRemoteEchoTrail(now);
            bool looksLikeOurRemoteMove = LooksLikeRecentRemoteEcho(x, y);

            if (!looksLikeOurRemoteMove)
            {
                m_HostLockUntilMs = Math.Max(m_HostLockUntilMs, now + HostLockMs);
                m_RemoteOwnerId = null;
                m_RemoteOwnerPriority = 0;
                m_RemoteOwnerLastInputMs = 0;
            }

            m_LastObservedX = x;
            m_LastObservedY = y;
        }
    }

    public int HostBlockRemainingMs()
    {
        lock (m_Gate)
        {
            long remaining = m_HostLockUntilMs - NowMs();
            return remaining > 0 ? ClampRetry(remaining) : 0;
        }
    }

    public PointerCursorState CursorStateFor(string sessionId)
    {
        lock (m_Gate)
        {
            int hostBlockMs = HostBlockRemainingMsUnsafe();
            if (hostBlockMs > 0)
                return new PointerCursorState("host", hostBlockMs);

            return m_RemoteOwnerId is null
                ? new PointerCursorState("host", 0)
                : new PointerCursorState(m_RemoteOwnerId == sessionId ? "self" : "remote", 0);
        }
    }

    public PointerControlView Snapshot()
    {
        lock (m_Gate)
        {
            long now = NowMs();
            int hostBlockMs = HostBlockRemainingMsUnsafe();
            if (hostBlockMs > 0)
                return new PointerControlView("host", null, hostBlockMs, 0);

            return m_RemoteOwnerId is null
                ? new PointerControlView("idle", null, 0, 0)
                : new PointerControlView(
                    "remote",
                    m_RemoteOwnerId,
                    0,
                    (int)Math.Max(0, now - m_RemoteOwnerLastInputMs));
        }
    }

    public static int PriorityFor(SessionRole role, GuestAccessLevel? guestAccessLevel) =>
        role == SessionRole.Owner
            ? 300
            : guestAccessLevel switch
            {
                GuestAccessLevel.Full => 200,
                GuestAccessLevel.Control => 100,
                _ => 0
            };

    private void SetRemoteOwner(string sessionId, int priority, long now)
    {
        m_RemoteOwnerId = sessionId;
        m_RemoteOwnerPriority = priority;
        m_RemoteOwnerLastInputMs = now;
    }

    private int HostBlockRemainingMsUnsafe()
    {
        long remaining = m_HostLockUntilMs - NowMs();
        return remaining > 0 ? ClampRetry(remaining) : 0;
    }

    private static int ClampRetry(long ms) => (int)Math.Clamp(ms, 100, 10_000);
    private static long NowMs() => Environment.TickCount64;

    private void PurgeRemoteEchoTrail(long now)
    {
        int removeCount = 0;
        while (removeCount < m_RemoteEchoTrail.Count
               && now - m_RemoteEchoTrail[removeCount].AtMs > RemoteEchoHistoryMs)
            removeCount++;

        if (removeCount > 0)
            m_RemoteEchoTrail.RemoveRange(0, removeCount);
    }

    private bool LooksLikeRecentRemoteEcho(int x, int y)
    {
        int toleranceSquared = RemoteEchoTolerancePx * RemoteEchoTolerancePx;
        for (int i = m_RemoteEchoTrail.Count - 1; i >= 0; i--)
        {
            var point = m_RemoteEchoTrail[i];
            if (DistanceSquared(x, y, point.X, point.Y) <= toleranceSquared)
                return true;
        }

        return false;
    }

    private static int DistanceSquared(int ax, int ay, int bx, int by)
    {
        int dx = ax - bx;
        int dy = ay - by;
        return dx * dx + dy * dy;
    }

    private readonly record struct RemoteEchoPoint(long AtMs, int X, int Y);
}
