using System.Security.Cryptography;

/// <summary>Protects the actions only the OpenMix window may take (settings, saved mixes, collection, card content,
/// mode changes, setup): each request must carry this run's secret token in the <c>X-OpenMix-Token</c> header, come
/// from this PC, and not come from another web site. The window gets the token in its URL fragment; phones never see
/// it, and a custom header can't be sent by another site's page without a CORS preflight, which OpenMix never allows.
/// Phone gameplay routes stay open to the local network.</summary>
static class HostGuard
{
    public static readonly string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>Routes phones use during play; everything else that changes state needs the token.</summary>
    static readonly string[] PhoneRoutes =
        { "/api/join", "/api/play", "/api/draw", "/api/clear", "/api/undo", "/api/team", "/api/action", "/api/button", "/api/leave" };

    public static string WindowUrl(string url) => url + "#t=" + Token;

    public static void Use(WebApplication app, bool allowPhoneRoutes)
    {
        app.Use(async (ctx, next) =>
        {
            var req = ctx.Request;
            bool changes = !HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method) && !HttpMethods.IsOptions(req.Method);
            if (changes && req.Path.StartsWithSegments("/api"))
            {
                bool phone = allowPhoneRoutes && PhoneRoutes.Any(p => req.Path.StartsWithSegments(p));
                if (!phone && !Allowed(ctx)) { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            }
            await next();
        });
    }

    static bool Allowed(HttpContext ctx)
    {
        if (ctx.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip)) return false;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.IsLoopback)) return false;
        var given = ctx.Request.Headers["X-OpenMix-Token"].ToString();
        return given.Length == Token.Length && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(given), System.Text.Encoding.ASCII.GetBytes(Token));
    }
}
