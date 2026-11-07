using ProjectHub.Application.Common;

namespace ProjectHub.Tests;

/// <summary>公共接口契约测试。</summary>
public sealed class ContractTests
{
    [Fact]
    public void ServiceResult_Ok_ShouldUseStableSuccessContract()
    {
        ServiceResult<int> result = ServiceResult<int>.Ok(7, "完成");

        Assert.True(result.Success);
        Assert.Equal("OK", result.Code);
        Assert.Equal("完成", result.Message);
        Assert.Equal(7, result.Data);
    }

    [Fact]
    public void BusinessException_ShouldKeepCodeAndHttpStatus()
    {
        BusinessException exception = new("无权限", "FORBIDDEN", 403);

        Assert.Equal("FORBIDDEN", exception.Code);
        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("无权限", exception.Message);
    }
}
