using System.Reflection;
using SqlSugar;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Tests;

/// <summary>报表模板 PostgreSQL JSON 持久化映射回归测试。</summary>
public sealed class ReportPersistenceTests
{
    [Theory]
    [InlineData(nameof(ReportTemplate.Columns), "columnsjson")]
    [InlineData(nameof(ReportTemplate.Filters), "filtersjson")]
    [InlineData(nameof(ReportTemplate.Sorts), "sortsjson")]
    public void ReportTemplate_JsonColumns_ShouldUseSqlSugarJsonMapping(string propertyName, string columnName)
    {
        PropertyInfo property = typeof(ReportTemplate).GetProperty(propertyName)
            ?? throw new InvalidOperationException($"未找到属性 {propertyName}");
        SugarColumn mapping = property.GetCustomAttribute<SugarColumn>()
            ?? throw new InvalidOperationException($"属性 {propertyName} 缺少 SugarColumn 映射");

        Assert.True(mapping.IsJson);
        Assert.Equal("jsonb", mapping.ColumnDataType);
        Assert.Equal(columnName, mapping.ColumnName);
        Assert.NotEqual(typeof(string), property.PropertyType);
    }
}
