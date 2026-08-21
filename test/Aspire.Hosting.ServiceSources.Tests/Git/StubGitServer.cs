using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The smallest HTTP server that makes libgit2 run its authentication handshake: it demands Basic
/// auth, then either refuses what it is given or answers with an empty upload-pack advertisement.
/// Only the handshake is served — no stub here is a real repository, so a clone against one always
/// ends in a <see cref="LibGit2Sharp.LibGit2SharpException"/> whatever the credentials.
/// </summary>
internal sealed class StubGitServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, bool> _accepts;
    private readonly List<string> _authorizations = [];
    private readonly Task _serving;

    private StubGitServer(Func<string, bool> accepts)
    {
        _accepts = accepts;

        var port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        RepositoryUrl = $"http://127.0.0.1:{port}/repo.git";
        _serving = Task.Run(Serve);
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

    public const string NoAuthorization = "<none>";

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

    private static byte[] EmptyUploadPackAdvertisement()
    {
        const string Service = "# service=git-upload-pack\n";
        return Encoding.UTF8.GetBytes($"{Service.Length + 4:x4}{Service}00000000");
    }

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

            if (authorization is null || !_accepts(authorization))
            {
                context.Response.StatusCode = 401;
                context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"stub\"");
                context.Response.Close();
                continue;
            }

            var body = EmptyUploadPackAdvertisement();
            context.Response.ContentType = "application/x-git-upload-pack-advertisement";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body);
            context.Response.Close();
        }
    }
}
