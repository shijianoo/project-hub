using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>新项目默认创建的资产工种分类。</summary>
internal static class ProjectAssetDefaults
{
    private static readonly (string Code, string Name, string Description)[] Definitions =
    [
        ("STRUCTURE", "结构图纸", "结构设计、工程图、装配图和相关计算资料"),
        ("MODELING", "三维建模", "三维模型、仿真模型、渲染文件和模型源文件"),
        ("HARDWARE", "硬件电路", "原理图、PCB、BOM、Gerber 和硬件调试资料"),
        ("FIRMWARE", "固件程序", "嵌入式源码、编译产物、烧录文件和版本说明"),
        ("SOFTWARE", "软件程序", "软件源码、安装包、部署包和运行配置"),
        ("DOCUMENT", "说明文档", "设计说明、测试报告、验收材料和过程文档"),
        ("MANUAL", "手册资料", "用户手册、维护手册、培训材料和交付清单"),
        ("OTHER", "其他产出", "其他无法归入既有工种的项目交付物")
    ];

    public static ProjectAssetCategory[] Create(long projectId) => Definitions
        .Select((item, index) => new ProjectAssetCategory
        {
            ProjectId = projectId,
            Code = item.Code,
            Name = item.Name,
            Description = item.Description,
            SortOrder = (index + 1) * 10
        })
        .ToArray();
}
