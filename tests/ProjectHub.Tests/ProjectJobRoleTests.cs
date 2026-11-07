using ProjectHub.Domain.Common;

namespace ProjectHub.Tests;

/// <summary>项目专业岗位位掩码转换测试。</summary>
public sealed class ProjectJobRoleTests
{
    [Fact]
    public void JobRoles_ShouldRoundTripThroughMask()
    {
        ProjectJobRole[] roles =
        [
            ProjectJobRole.HardwareEngineering,
            ProjectJobRole.FirmwareEngineering,
            ProjectJobRole.TestEngineering
        ];

        long mask = roles.ToMask();
        IReadOnlyList<ProjectJobRole> restoredRoles = mask.ToJobRoles();

        Assert.True(mask.IsValidProjectJobRoleMask());
        Assert.Equal(roles, restoredRoles);
    }

    [Fact]
    public void UndefinedFlag_ShouldBeRejected()
    {
        long undefinedMask = ProjectJobRoleExtensions.AllDefinedMask | (1L << 40);

        Assert.False(undefinedMask.IsValidProjectJobRoleMask());
    }
}
