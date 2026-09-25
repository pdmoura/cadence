using Cadence.Web.Data;
using Cadence.Web.Models;

namespace Cadence.Web.Services;

/// <summary>
/// Who is acting. Cadence is an internal tool that sits behind Cloudflare Access in production, so the
/// identity is a cookie-selected team member rather than a password login. Every write records this id.
/// </summary>
public sealed class CurrentMember(IHttpContextAccessor accessor, MemberRepository members)
{
    public const string CookieName = "cadence_member";
    private Member? _cached;

    public async Task<Member?> GetAsync()
    {
        if (_cached is not null) return _cached;
        var ctx = accessor.HttpContext;
        if (ctx is null) return null;
        var all = await members.AllAsync();
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var raw) && long.TryParse(raw, out var id))
            _cached = all.FirstOrDefault(m => m.Id == id);
        _cached ??= all.FirstOrDefault(m => m.Role == MemberRole.Developer) ?? all.FirstOrDefault();
        return _cached;
    }

    public async Task<long?> IdAsync() => (await GetAsync())?.Id;
}
