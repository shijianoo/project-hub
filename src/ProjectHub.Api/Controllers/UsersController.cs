using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>轻量用户账号与角色管理接口。</summary>
[ApiController]
[Route("api/users")]
[Authorize]
public sealed class UsersController(IUserService userService) : ControllerBase
{
    /// <summary>分页查询用户账号，仅管理员可用。</summary>
    [HttpGet]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ServiceResult<PagedResult<UserDto>>>> GetPageAsync([FromQuery] UserQuery query)
    {
        PagedResult<UserDto> result = await userService.GetPageAsync(query);
        return Ok(ServiceResult<PagedResult<UserDto>>.Ok(result));
    }

    /// <summary>获取配置项目成员时使用的启用用户选项。</summary>
    [HttpGet("options")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<UserDto>>>> GetOptionsAsync()
    {
        IReadOnlyList<UserDto> users = await userService.GetOptionsAsync();
        return Ok(ServiceResult<IReadOnlyList<UserDto>>.Ok(users));
    }

    /// <summary>创建用户账号并分配角色。</summary>
    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ServiceResult<UserDto>>> CreateAsync(CreateUserRequest request)
    {
        UserDto user = await userService.CreateAsync(request);
        return Created($"api/users/{user.Id}", ServiceResult<UserDto>.Ok(user, "用户创建成功"));
    }

    /// <summary>编辑用户基本信息、状态和角色。</summary>
    [HttpPut("{id:long}")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ServiceResult<UserDto>>> UpdateAsync(long id, UpdateUserRequest request)
    {
        UserDto user = await userService.UpdateAsync(id, request);
        return Ok(ServiceResult<UserDto>.Ok(user, "用户更新成功"));
    }

    /// <summary>管理员重置用户密码，并使该用户的刷新令牌全部失效。</summary>
    [HttpPut("{id:long}/password")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ServiceResult<object>>> ResetPasswordAsync(long id, ResetPasswordRequest request)
    {
        await userService.ResetPasswordAsync(id, request);
        return Ok(ServiceResult<object>.Ok(new { }, "密码重置成功"));
    }
}
