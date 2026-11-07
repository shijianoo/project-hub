namespace ProjectHub.Domain.Common;

/// <summary>提供项目专业岗位数组与数据库位掩码之间的标准转换。</summary>
public static class ProjectJobRoleExtensions
{
    private static readonly ProjectJobRole[] DefinedRoles = Enum.GetValues<ProjectJobRole>()
        .Where(role => role != ProjectJobRole.None)
        .ToArray();

    /// <summary>全部有效专业岗位组成的位掩码。</summary>
    public static readonly long AllDefinedMask = DefinedRoles.Aggregate(0L, (mask, role) => mask | (long)role);

    /// <summary>将专业岗位集合转换为便于数据库检索和持久化的位掩码。</summary>
    public static long ToMask(this IEnumerable<ProjectJobRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Aggregate(0L, (mask, role) => mask | (long)role);
    }

    /// <summary>将数据库位掩码还原为稳定排序的专业岗位数组。</summary>
    public static IReadOnlyList<ProjectJobRole> ToJobRoles(this long mask) => DefinedRoles
        .Where(role => (mask & (long)role) == (long)role)
        .ToArray();

    /// <summary>判断位掩码是否至少包含一个岗位，且不含未定义的标志位。</summary>
    public static bool IsValidProjectJobRoleMask(this long mask) =>
        mask > 0 && (mask & ~AllDefinedMask) == 0;
}
