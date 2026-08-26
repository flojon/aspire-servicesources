using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The smallest HTTP server that makes libgit2 run its authentication handshake: it demands Basic
/// auth, then either refuses what it is given or advertises a single branch. The advertisement names
/// a commit no stub can actually serve, so a clone against one always ends in a
/// <see cref="LibGit2Sharp.LibGit2SharpException"/> whatever the credentials — but it ends there
/// having made both requests of a real clone, the ref advertisement and the pack POST, which is what
/// lets a credential re-challenge on the second request be observed at all.
/// </summary>
internal sealed class StubGitServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, bool> _accepts;
    private readonly List<string> _authorizations = [];
    private readonly List<string> _requests = [];
    private readonly Task _serving;

    private StubGitServer(Func<string, bool> accepts)
    {
        _accepts = accepts;

        var port = BindToFreePort(FreePort, BindListener);
        RepositoryUrl = $"http://127.0.0.1:{port}/repo.git";
        _serving = Task.Run(Serve);

        void BindListener(int candidate)
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            _listener.Start();
        }
    }

    public string RepositoryUrl { get; }

    /// <summary>
    /// The <c>Authorization</c> header of each request received, in order, with
    /// <see cref="NoAuthorization"/> standing in for a request that carried none.
    /// </summary>
    public IReadOnlyList<string> Authorizations
    {
        get
        {
            lock (_authorizations)
            {
                return [.. _authorizations];
            }
        }
    }

    /// <summary>
    /// The method and path of each request received, in order (<c>"GET /repo.git/info/refs"</c>), so
    /// a test can assert which stage of the clone it actually got to rather than trusting that a
    /// still-parseable advertisement carried it there.
    /// </summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public const string NoAuthorization = "<none>";

    /// <summary>The pack request a clone makes once it has the ref advertisement.</summary>
    public const string PackRequest = "POST /repo.git/git-upload-pack";

    public static StubGitServer RefusingEverything() => new(_ => false);

    public static StubGitServer Accepting(string username, string password) =>
        new(authorization => authorization == BasicAuthorization(username, password));

    public static string BasicAuthorization(string username, string password) =>
        $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}";

    public void Dispose()
    {
        _listener.Close();
        _serving.Wait(TimeSpan.FromSeconds(10));
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Picks a port and binds it, retrying on a fresh port if the bind fails. <see cref="FreePort"/>
    /// releases its probe socket before <paramref name="bind"/> claims the same port, leaving a
    /// window where something else — including a sibling TFM's copy of this same test, since the
    /// suite runs net8/9/10 concurrently — can take it first. The window can't be closed, only
    /// tolerated: retry a bounded number of times rather than let the collision fail the test.
    /// </summary>
    internal static int BindToFreePort(Func<int> pickPort, Action<int> bind, int maxAttempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = pickPort();
            try
            {
                bind(port);
                return port;
            }
            catch (HttpListenerException) when (attempt < maxAttempts)
            {
            }
        }
    }

    /// <summary>A commit id to advertise. Nothing can fetch it; it only has to be well formed.</summary>
    private const string AdvertisedCommit = "1234567890abcdef1234567890abcdef12345678";

    private static string PktLine(string payload) => $"{payload.Length + 4:x4}{payload}";

    /// <summary>
    /// A ref advertisement for a single branch, with the <c>HEAD</c> symref a clone needs to pick a
    /// default branch — without it libgit2 gives up before asking for the pack, and the second
    /// request would never be made.
    /// </summary>
    private static byte[] UploadPackAdvertisement()
    {
        const string Capabilities = "multi_ack_detailed thin-pack ofs-delta agent=stub/1";

        var advertisement =
            PktLine("# service=git-upload-pack\n")
            + "0000"
            + PktLine($"{AdvertisedCommit} HEAD\0{Capabilities} symref=HEAD:refs/heads/main\n")
            + PktLine($"{AdvertisedCommit} refs/heads/main\n")
            + "0000";

        return Encoding.UTF8.GetBytes(advertisement);
    }

    /// <summary>
    /// Enough of a pack response to acknowledge the request and then end: the clone fails on the
    /// truncated pack that follows, which is fine — the request having been made at all is the point.
    /// </summary>
    private static byte[] UploadPackResult() => Encoding.UTF8.GetBytes(PktLine("NAK\n"));

    private void Serve()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var authorization = context.Request.Headers["Authorization"];
            lock (_authorizations)
            {
                _authorizations.Add(authorization ?? NoAuthorization);
            }

            var request = $"{context.Request.HttpMethod} {context.Request.Url?.AbsolutePath}";
            lock (_requests)
            {
                _requests.Add(request);
            }

            // Drain the body whatever happens to it, so the client is never left writing a pack
            // request into a socket nobody is reading.
            using (var body = context.Request.InputStream)
            {
                body.CopyTo(Stream.Null);
            }

            if (authorization is null || !_accepts(authorization))
            {
                context.Response.StatusCode = 401;
                context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"stub\"");
                context.Response.Close();
                continue;
            }

            var isPackRequest = request == PackRequest;
            var responseBody = isPackRequest ? UploadPackResult() : UploadPackAdvertisement();
            context.Response.ContentType = isPackRequest
                ? "application/x-git-upload-pack-result"
                : "application/x-git-upload-pack-advertisement";
            context.Response.ContentLength64 = responseBody.Length;
            context.Response.OutputStream.Write(responseBody);
            context.Response.Close();
        }
    }
}
