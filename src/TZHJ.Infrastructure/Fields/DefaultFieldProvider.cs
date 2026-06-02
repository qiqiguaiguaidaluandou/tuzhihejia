using TZHJ.Core.Contracts;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;
using TZHJ.Core.Schemas;

namespace TZHJ.Infrastructure.Fields;

/// <summary>
/// 默认字段提供者：返回内置 schema。可被登录后下发的 ClientConfig 覆盖——
/// 调用 <see cref="Apply"/> 用配置里的字段集替换，UI/存储随之改变（加字段不改代码）。
/// </summary>
public sealed class DefaultFieldProvider : IFieldProvider
{
    private IReadOnlyList<FieldDefinition> _pricing = FieldSchemas.Pricing;
    private IReadOnlyList<FieldDefinition> _drawing = FieldSchemas.DrawingSelection;

    public IReadOnlyList<FieldDefinition> FieldsFor(FlowType flow) =>
        flow == FlowType.Pricing ? _pricing : _drawing;

    /// <summary>登录后用下发配置覆盖字段集。</summary>
    public void Apply(ClientConfig config)
    {
        if (config.PricingFields.Count > 0) _pricing = config.PricingFields;
        if (config.DrawingSelectionFields.Count > 0) _drawing = config.DrawingSelectionFields;
    }
}
