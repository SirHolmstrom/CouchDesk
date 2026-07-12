using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Core.Branding;
using Core.Capture;
using Core.Config;
using Core.Files;
using Core.Logging;
using Core.RemoteAccess;
using Core.Security;
using Core.Streaming;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;

namespace Core.Hosting;

public sealed class RemoteDesktopHost : IAsyncDisposable
{
    private const int MaxSessions = 4;
    private const string CookieName = "rd_session";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppConfig m_Config;
    private readonly NetworkInfoService m_Network;
    private readonly GuestInviteStore m_GuestInvites = new();
    private readonly SessionStore m_Sessions;
    private readonly LoginThrottle m_Throttle = new();
    private readonly StreamSessionRegistry m_Registry = new();
    private readonly PointerInputArbiter m_PointerInput = new();
    private readonly GdiScreenCapturer m_Capturer = new();
    private readonly ConcurrentDictionary<string, long> m_LastSpikeLogAt = new();
    private WebApplication? m_App;

    public string LanUrl => $"https://{LanIp}:{Port}";
    public string HostnameUrl => $"https://{Dns.GetHostName()}.local:{Port}";
    public bool IsRunning { get; private set; }
    public IPAddress LanIp { get; private set; }
    public int Port => m_Config.Port;
    public StreamSessionRegistry Sessions => m_Registry;
    public SessionStore LoginSessions => m_Sessions;
    public GuestInviteStore GuestInvites => m_GuestInvites;
    public PointerInputArbiter PointerInput => m_PointerInput;

    public RemoteDesktopHost(AppConfig config, NetworkInfoService network)
    {
        m_Config = config;
        m_Network = network;
        LanIp = network.GetLanIp();
        m_Sessions = new SessionStore(
            TimeSpan.FromMinutes(config.SessionTimeoutMinutes),
            m_GuestInvites.IsActive);
    }

    public GuestInvite CreateGuestInvite(GuestAccessLevel accessLevel, TimeSpan lifetime)
    {
        var invite = m_GuestInvites.Create(accessLevel, lifetime);
        AuditLogger.Log(
            "GUEST_INVITE_CREATE",
            "local",
            $"id={invite.Id:N} access={accessLevel} expires={invite.ExpiresUtc:O}");
        return invite;
    }

    public bool RevokeGuestInvite(Guid inviteId)
    {
        bool revoked = m_GuestInvites.Revoke(inviteId);
        m_Sessions.RevokeGuestInvite(inviteId);
        m_Registry.DisconnectGuestInvite(inviteId);
        if (revoked) AuditLogger.Log("GUEST_INVITE_REVOKE", "local", $"id={inviteId:N}");
        return revoked;
    }

    public void RevokeAllGuestInvites()
    {
        foreach (var invite in m_GuestInvites.Snapshot()) RevokeGuestInvite(invite.Id);
    }

    public int GuestSessionCount(Guid inviteId) => m_Registry.CountForGuestInvite(inviteId);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;

        LanIp = m_Network.GetLanIp();
        string hostname = Dns.GetHostName();
        var certificate = Certificates.LoadOrCreate($"{hostname}.local", LanIp);
        Certificates.EnsureTrusted(certificate);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 1L << 30;
            IPAddress bindAddress = IPAddress.TryParse(m_Config.BindAddress, out var parsed)
                ? parsed
                : IPAddress.Any;
            kestrel.Listen(bindAddress, m_Config.Port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
                listenOptions.UseHttps(certificate);
            });
        });

        var app = builder.Build();
        MapRoutes(app);
        ConfigureFrontend(app);

        await app.StartAsync(cancellationToken);
        m_App = app;
        IsRunning = true;
        AuditLogger.Log("HOST_START", "local", LanUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (m_App is null) return;

        m_Registry.DisconnectAll();
        m_Sessions.RevokeAll();
        await m_App.StopAsync(cancellationToken);
        await m_App.DisposeAsync();
        m_App = null;
        IsRunning = false;
        AuditLogger.Log("HOST_STOP", "local");
    }

    private void MapRoutes(WebApplication app)
    {
        string ClientIp(HttpContext context) =>
            context.Connection.RemoteIpAddress?.ToString() ?? "?";
        SessionInfo? LoginSession(HttpContext context) =>
            m_Sessions.TryGet(context.Request.Cookies[CookieName], out var session) ? session : null;
        bool IsAuthed(HttpContext context) => LoginSession(context) is not null;
        bool IsOwner(HttpContext context) => LoginSession(context)?.Role == SessionRole.Owner;
        bool IsLanClient(HttpContext context) =>
            m_Network.IsLanAddress(context.Connection.RemoteIpAddress);
        bool CanStream(HttpContext context) =>
            IsAuthed(context) && (IsLanClient(context) || m_Config.RemoteAccessEnabled);
        void SetSessionCookie(HttpContext context, string token) =>
            context.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });

        app.MapGet("/api/about", context =>
            context.Response.WriteAsJsonAsync(new
            {
                productName = ProductInfo.Name,
                hostDisplayName = HostDisplayName.Get(m_Config),
                isConfigured = m_Config.IsConfigured
            }));

        app.MapGet("/setup", async context =>
        {
            if (m_Config.IsConfigured)
            {
                context.Response.Redirect("/login.html");
                return;
            }

            await context.Response.SendFileAsync(Path.Combine(GetWebRoot(), "setup.html"));
        });

        app.MapPost("/api/setup", async context =>
        {
            if (m_Config.IsConfigured)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            string password = body?.GetValueOrDefault("password") ?? "";
            if (password.Length < 12)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Password must be at least 12 characters.");
                return;
            }

            m_Config.PasswordHash = PasswordHasher.Hash(password);
            m_Config.HostDisplayName = HostDisplayName.NormalizeCustom(
                body?.GetValueOrDefault("hostDisplayName"));
            m_Config.BindAddress = LanIp.ToString();
            m_Config.Save();
            AuditLogger.Log("SETUP_COMPLETE", ClientIp(context));
        });

        app.MapPost("/api/login", async context =>
        {
            string ip = ClientIp(context);
            var (blocked, retryAfter) = m_Throttle.CheckLockout(ip);
            if (blocked)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                AuditLogger.Log("LOGIN_BLOCKED", ip);
                return;
            }

            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            string password = body?.GetValueOrDefault("password") ?? "";
            if (m_Config.PasswordHash is null
                || !PasswordHasher.Verify(password, m_Config.PasswordHash))
            {
                m_Throttle.RecordFailure(ip);
                AuditLogger.Log("LOGIN_FAIL", ip);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            m_Throttle.RecordSuccess(ip);
            string token = m_Sessions.CreateOwner(ip);
            SetSessionCookie(context, token);
            AuditLogger.Log("LOGIN_OK", ip);
        });

        app.MapPost("/api/guest-login", async context =>
        {
            string ip = ClientIp(context);
            var (blocked, retryAfter) = m_Throttle.CheckLockout(ip);
            if (blocked)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                AuditLogger.Log("GUEST_LOGIN_BLOCKED", ip);
                return;
            }

            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            string code = body?.GetValueOrDefault("code") ?? "";
            if (!m_GuestInvites.TryRedeem(code, out var invite))
            {
                m_Throttle.RecordFailure(ip);
                AuditLogger.Log("GUEST_LOGIN_FAIL", ip);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            m_Throttle.RecordSuccess(ip);
            string token = m_Sessions.CreateGuest(ip, invite.Id, invite.AccessLevel);
            SetSessionCookie(context, token);
            AuditLogger.Log(
                "GUEST_LOGIN_OK", ip, $"invite={invite.Id:N} access={invite.AccessLevel}");
        });

        app.MapPost("/api/logout", context =>
        {
            m_Sessions.Revoke(context.Request.Cookies[CookieName]);
            context.Response.Cookies.Delete(CookieName);
            AuditLogger.Log("LOGOUT", ClientIp(context));
            return Task.CompletedTask;
        });

        app.MapGet("/api/session", context =>
        {
            var session = LoginSession(context);
            if (session is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            return context.Response.WriteAsJsonAsync(new
            {
                role = session.Role.ToString().ToLowerInvariant(),
                accessLevel = session.GuestAccessLevel?.ToString().ToLowerInvariant() ?? "owner",
                permissions = session.Permissions
            });
        });

        // A lightweight RTT probe on a separate HTTP connection. Unlike the WebSocket
        // pong, this response cannot sit behind a large outbound video frame.
        app.MapGet("/api/ping", context =>
        {
            if (LoginSession(context) is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        app.MapPost("/api/diagnostics/spike", async context =>
        {
            if (LoginSession(context) is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (context.Request.ContentLength is > 8192)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            StreamSpikeReport? report;
            try
            {
                report = await JsonSerializer.DeserializeAsync<StreamSpikeReport>(
                    context.Request.Body,
                    WebJsonOptions,
                    context.RequestAborted);
            }
            catch (JsonException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (report is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            string ip = ClientIp(context);
            long now = Environment.TickCount64;
            if (m_LastSpikeLogAt.TryGetValue(ip, out long last) && now - last < 5000)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            m_LastSpikeLogAt[ip] = now;

            string codec = report.Codec?.Equals("jpeg", StringComparison.OrdinalIgnoreCase) == true
                ? "jpeg"
                : report.Codec?.Equals("h264", StringComparison.OrdinalIgnoreCase) == true
                    ? "h264"
                    : "unknown";
            AuditLogger.Log(
                "STREAM_SPIKE",
                ip,
                $"controlMs={Metric(report.ControlRttMs)} netMs={Metric(report.NetworkRttMs)} "
                + $"netAgeMs={Metric(report.NetworkSampleAgeMs)} "
                + $"capturePeakMs={Metric(report.CapturePeakMs)} "
                + $"acquirePeakMs={Metric(report.CaptureAcquirePeakMs)} "
                + $"copyPeakMs={Metric(report.CaptureCopyPeakMs)} mapPeakMs={Metric(report.CaptureMapPeakMs)} "
                + $"cpuCopyPeakMs={Metric(report.CaptureCpuCopyPeakMs)} encodePeakMs={Metric(report.EncodePeakMs)} "
                + $"controlSendWaitPeakMs={Metric(report.ControlSendWaitPeakMs)} controlSendPeakMs={Metric(report.ControlSendPeakMs)} "
                + $"sendWaitPeakMs={Metric(report.SendWaitPeakMs)} sendPeakMs={Metric(report.SendPeakMs)} "
                + $"hostQueuePeak={Math.Clamp(report.HostQueuePeak, 0, 10000)} "
                + $"framePeakKb={Metric(report.FramePeakBytes / 1024.0)} "
                + $"videoKbps={Metric(report.VideoKbps)} targetKbps={Math.Clamp(report.TargetVideoBitrateKbps, 0, 100000)} "
                + $"appliedKbps={Math.Clamp(report.AppliedVideoBitrateKbps, 0, 100000)} "
                + $"videoFrames={Math.Clamp(report.VideoFrames, 0, 10000)} paced={(report.PacedFrameSeen ? 1 : 0)} "
                + $"decoderQueue={Math.Clamp(report.DecoderQueue, 0, 10000)} "
                + $"paintPeakMs={Metric(report.PaintPeakMs)} paintGapPeakMs={Metric(report.PaintGapPeakMs)} "
                + $"browserLoopPeakMs={Metric(report.BrowserEventLoopPeakMs)} browserLongTaskPeakMs={Metric(report.BrowserLongTaskPeakMs)} "
                + $"hostTimerPeakMs={Metric(report.HostTimerPeakMs)} "
                + $"cpu={Metric(report.ProcessCpuPercent)}/{Metric(report.SystemCpuPercent)} "
                + $"threadPool={Math.Clamp(report.ThreadPoolQueue, 0, 100000)}/{Math.Clamp(report.ThreadPoolThreads, 0, 10000)} "
                + $"keyframe={(report.KeyframeSeen ? 1 : 0)} "
                + $"gc={Math.Clamp(report.Gc0, 0, 10000)}/{Math.Clamp(report.Gc1, 0, 10000)}/{Math.Clamp(report.Gc2, 0, 10000)} "
                + $"codec={codec} fps={Math.Clamp(report.Fps, 1, 60)}");
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        app.MapPost("/api/upload", async context =>
        {
            var session = LoginSession(context);
            if (session is null || !session.Permissions.CanTransferFiles)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            string action = context.Request.Query["action"].ToString();
            string name = context.Request.Query["name"].ToString();
            string saved;
            try
            {
                saved = await Inbox.SaveAsync(context.Request.Body, name);
            }
            catch (Exception ex)
            {
                AuditLogger.Log("UPLOAD_ERROR", ClientIp(context), ex.Message);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            try
            {
                if (action == "open") Inbox.OpenFolder();
            }
            catch (Exception ex)
            {
                AuditLogger.Log("UPLOAD_ACTION_ERROR", ClientIp(context), ex.Message);
            }

            AuditLogger.Log("UPLOAD", ClientIp(context), $"{Path.GetFileName(saved)} action={action}");
            await context.Response.WriteAsJsonAsync(new { ok = true, savedAs = Path.GetFileName(saved) });
        });

        app.MapGet("/api/frame", context =>
        {
            // LAN streaming is always available after authentication. The public
            // access switch is independent of router/firewall state, so stale rules
            // cannot expose frames to non-LAN clients.
            if (!CanStream(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            var frame = m_Capturer.CaptureJpeg(0, m_Config.JpegQuality);
            context.Response.ContentType = "image/jpeg";
            return context.Response.Body.WriteAsync(frame.JpegBytes).AsTask();
        });

        app.MapGet("/ws/control", async context =>
        {
            var loginSession = LoginSession(context);
            string token = context.Request.Query["token"].ToString();
            if (loginSession is null
                || (!IsLanClient(context) && !m_Config.RemoteAccessEnabled)
                || string.IsNullOrWhiteSpace(token)
                || !m_Registry.TryGetByControlToken(token, out var session)
                || !session.MatchesLoginSession(loginSession))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await session.RunControlSocketAsync(socket, context.RequestAborted);
        });

        app.MapGet("/ws", async context =>
        {
            string loginToken = context.Request.Cookies[CookieName] ?? "";
            var loginSession = LoginSession(context);
            if (loginSession is null || (!IsLanClient(context) && !m_Config.RemoteAccessEnabled))
            {
                AuditLogger.Log(
                    "STREAM_DENY",
                    ClientIp(context),
                    $"authenticated={IsAuthed(context)} lan={IsLanClient(context)} remoteEnabled={m_Config.RemoteAccessEnabled}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (m_Registry.Count >= MaxSessions)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var session = new StreamSession(
                socket,
                m_Config,
                ClientIp(context),
                IsLanClient(context),
                loginSession,
                m_PointerInput,
                () => loginSession.GuestInviteId is not Guid inviteId
                    || m_GuestInvites.IsActive(inviteId),
                () => m_Sessions.TryGet(loginToken, out _),
                () => m_Sessions.Revoke(loginToken),
                context.RequestAborted);
            m_Registry.Add(session);
            try { await session.RunAsync(); }
            finally
            {
                m_Registry.Remove(session.Id);
                session.Dispose();
            }
        });

        app.MapGet("/api/clients", context =>
        {
            if (!IsOwner(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            return context.Response.WriteAsJsonAsync(m_Registry.Snapshot());
        });

        app.MapPost("/api/clients/{id}/disconnect", (HttpContext context, string id) =>
        {
            if (!IsOwner(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            bool disconnected = m_Registry.Disconnect(id);
            AuditLogger.Log("CLIENT_KICKED", ClientIp(context), $"id={id} ok={disconnected}");
            context.Response.StatusCode = disconnected
                ? StatusCodes.Status200OK
                : StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }

    private void ConfigureFrontend(WebApplication app)
    {
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            // A stale router/firewall rule must not leave login, upload, or static
            // application routes reachable after public access is switched off.
            bool isLanClient = m_Network.IsLanAddress(context.Connection.RemoteIpAddress);
            if (!isLanClient && !m_Config.RemoteAccessEnabled)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });
        app.Use(async (context, next) =>
        {
            if (!m_Config.IsConfigured
                && !context.Request.Path.StartsWithSegments("/setup")
                && !context.Request.Path.StartsWithSegments("/api/setup")
                && !context.Request.Path.StartsWithSegments("/api/about"))
            {
                context.Response.Redirect("/setup");
                return;
            }
            await next();
        });

        app.Use(async (context, next) =>
        {
            if ((context.Request.Path == "/index.html" || context.Request.Path == "/")
                && m_Config.IsConfigured
                && !m_Sessions.Validate(context.Request.Cookies[CookieName]))
            {
                context.Response.Redirect("/login.html");
                return;
            }
            await next();
        });

        var fileProvider = new PhysicalFileProvider(GetWebRoot());
        var defaultFiles = new DefaultFilesOptions { FileProvider = fileProvider };
        defaultFiles.DefaultFileNames.Clear();
        defaultFiles.DefaultFileNames.Add("login.html");
        app.UseDefaultFiles(defaultFiles);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Context.Response.Headers.Pragma = "no-cache";
                context.Context.Response.Headers.Expires = "0";
            }
        });
    }

    private static string GetWebRoot()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "web");
        if (Directory.Exists(packaged)) return packaged;

        string project = Path.Combine(Directory.GetCurrentDirectory(), "src", "Core", "web");
        if (Directory.Exists(project)) return project;

        throw new DirectoryNotFoundException("The packaged web frontend could not be found.");
    }

    private static string Metric(double value) =>
        Math.Clamp(double.IsFinite(value) ? value : 0, 0, 60_000)
            .ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record StreamSpikeReport(
        double ControlRttMs,
        double NetworkRttMs,
        double NetworkSampleAgeMs,
        double CapturePeakMs,
        double CaptureAcquirePeakMs,
        double CaptureCopyPeakMs,
        double CaptureMapPeakMs,
        double CaptureCpuCopyPeakMs,
        double EncodePeakMs,
        double ControlSendWaitPeakMs,
        double ControlSendPeakMs,
        double SendWaitPeakMs,
        double SendPeakMs,
        int HostQueuePeak,
        int FramePeakBytes,
        double VideoKbps,
        int VideoFrames,
        int TargetVideoBitrateKbps,
        int AppliedVideoBitrateKbps,
        bool PacedFrameSeen,
        int DecoderQueue,
        double PaintPeakMs,
        double PaintGapPeakMs,
        double BrowserEventLoopPeakMs,
        double BrowserLongTaskPeakMs,
        double HostTimerPeakMs,
        double ProcessCpuPercent,
        double SystemCpuPercent,
        long ThreadPoolQueue,
        int ThreadPoolThreads,
        bool KeyframeSeen,
        int Gc0,
        int Gc1,
        int Gc2,
        string? Codec,
        int Fps);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        m_Capturer.Dispose();
    }
}
