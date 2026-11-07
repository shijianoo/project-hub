using ProjectHub.Domain.Common;

namespace ProjectHub.Application.Common;

/// <summary>当前用户在单个项目中的有效权限。</summary>
public sealed record ProjectAccess(ProjectRole? ProjectRole, bool CanView, bool CanManage, bool CanEnterTime);
