using System.Net;
using System.Security.Claims;

namespace Library.Web.Auth;

/// <summary>
/// Lets direct visitors from <see cref="LibraryOptions.TrustedNetworks"/> in without logging in. A request that came
/// through a proxy never qualifies: a reverse proxy usually sits on the LAN, so without this rule every visitor from the
/// internet would look local.
/// </summary>
public static class TrustedNetwork
{
    public const string AuthenticationType = "TrustedNetwork";

    private const string DirectPeerKey = "library.direct-peer";
    private const string ProxiedKey = "library.proxied";

    private static readonly string[] ForwardingHeaders =
        ["X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Real-IP", "Forwarded"];

    /// <summary>Must run before the forwarded-headers middleware rewrites the client address.</summary>
    public static void RecordConnection(HttpContext context)
    {
        context.Items[DirectPeerKey] = context.Connection.RemoteIpAddress;
        context.Items[ProxiedKey] = ForwardingHeaders.Any(h => context.Request.Headers.ContainsKey(h));
    }

    /// <summary>Runs after authentication: an anonymous direct LAN visitor becomes a "home network" user.</summary>
    public static void SignInIfTrusted(HttpContext context, LibraryOptions options)
    {
        if (context.User.Identity?.IsAuthenticated == true || !IsTrusted(context, options))
        {
            return;
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Home network")], AuthenticationType));
    }

    public static bool IsHomeNetworkUser(ClaimsPrincipal user) => user.Identity?.AuthenticationType == AuthenticationType;

    /// <summary>
    /// Whether this visitor may create the first account: a direct (not proxied) connection from a trusted network,
    /// or from anywhere direct when no trusted networks are configured. Never through the reverse proxy, so the
    /// internet can't claim a fresh install.
    /// </summary>
    public static bool MayRunSetup(HttpContext context, LibraryOptions options) =>
        context.Items[ProxiedKey] is false &&
        (options.TrustedNetworks.Count == 0 || IsTrusted(context, options));

    public static bool IsTrusted(HttpContext context, LibraryOptions options)
    {
        if (options.TrustedNetworks.Count == 0 ||
            context.Items[ProxiedKey] is not false ||
            context.Items[DirectPeerKey] is not IPAddress peer)
        {
            return false;
        }

        var address = Normalize(peer);
        return !options.KnownProxies.Any(p => Normalize(p).Equals(address)) &&
               options.TrustedNetworks.Any(n => n.Contains(address));
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
