namespace ProjectHub.Application.Common;

/// <summary>当前请求中的登录用户信息。</summary>
public sealed record CurrentUser(long UserId, string UserName, string DisplayName, IReadOnlyList<string> Roles)
{
    /// <summary>是否拥有指定角色。</summary>
    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

/// <summary>为应用服务提供与 Web 框架无关的当前用户访问入口。</summary>
public interface ICurrentUserAccessor
{
    /// <summary>读取当前登录用户，未登录时抛出未授权异常。</summary>
    CurrentUser GetRequiredUser();

    /// <summary>尝试读取当前登录用户。</summary>
    CurrentUser? GetUser();
}
