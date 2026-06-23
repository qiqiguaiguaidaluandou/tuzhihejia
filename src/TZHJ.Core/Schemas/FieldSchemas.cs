using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.Core.Schemas;

/// <summary>
/// 首批字段集（来自方案设计 §2/§4）。字段配置化、加字段不改代码：
/// 现在是硬编码的"默认 schema"，将来由后端 IConfigGateway 下发覆盖。
/// </summary>
public static class FieldSchemas
{
    // —— 列键常量（稳定标识，用于行值字典 / xlsx 回读映射）——
    public static class PricingKeys
    {
        public const string MaterialCode = "materialCode"; // 物料编码（行标识键）
        public const string Model = "model";               // 型号
        public const string Name = "name";                 // 名称
        public const string MaterialDesc = "materialDesc"; // 物料描述（EBS ITEM_DESC）
        public const string DemandQty = "demandQty";       // 需求数量
        public const string HasChange = "hasChange";       // 当前是否存在变更（PLM）
        public const string TargetPrice = "targetPrice";   // 目标价（手填、必填）
    }

    public static class DrawingKeys
    {
        public const string EbsId = "ebsId";               // EBS-ID（行标识键 / 回传关联键）
        public const string InvOrg = "invOrg";             // 库存组织
        public const string SourceNo = "sourceNo";         // 来源单号
        public const string Project = "project";           // 项目
        public const string ProductLine = "productLine";   // 产品线
        public const string PlanNo = "planNo";             // 方案编号
        public const string DeptDesc = "deptDesc";         // 部门描述
        public const string MaterialCode = "materialCode"; // 物料编码
        public const string MaterialDesc = "materialDesc"; // 物料描述
        public const string CurrentQty = "currentQty";     // 当前数量
        public const string CreateDate = "createDate";     // 创建日期
        public const string DemandDate = "demandDate";     // 需求日期
        public const string Applicant = "applicant";       // 申请人名称
        public const string Remark = "remark";             // 备注
        public const string HasChange = "hasChange";       // 是否存在变更（PLM）
        public const string CanMachine = "canMachine";     // 是否机加中心可以做（手填、是/否、必填）
    }

    /// <summary>核价表单字段：物料编码/型号/名称/物料描述/需求数量(EBS) + 当前是否存在变更(PLM) + 目标价(手填、必填)。</summary>
    public static IReadOnlyList<FieldDefinition> Pricing { get; } = new[]
    {
        new FieldDefinition { Key = PricingKeys.MaterialCode, DisplayName = "物料编码", Source = FieldSource.Ebs, Order = 1, IsRowKey = true },
        new FieldDefinition { Key = PricingKeys.Model,        DisplayName = "型号",     Source = FieldSource.Ebs, Order = 2 },
        new FieldDefinition { Key = PricingKeys.Name,         DisplayName = "名称",     Source = FieldSource.Ebs, Order = 3 },
        new FieldDefinition { Key = PricingKeys.MaterialDesc, DisplayName = "物料描述", Source = FieldSource.Ebs, Order = 4 },
        new FieldDefinition { Key = PricingKeys.DemandQty,    DisplayName = "需求数量", Source = FieldSource.Ebs, Order = 5 },
        new FieldDefinition { Key = PricingKeys.HasChange,    DisplayName = "当前是否存在变更", Source = FieldSource.Plm, Order = 6 },
        new FieldDefinition { Key = PricingKeys.TargetPrice,  DisplayName = "目标价",   Source = FieldSource.Manual, Editor = FieldEditor.Number, IsRequired = true, Order = 7 },
    };

    /// <summary>挑图表单字段：13 个 EBS 只读 + 是否存在变更(PLM) + 是否机加中心可以做(手填、是/否、必填)。</summary>
    public static IReadOnlyList<FieldDefinition> DrawingSelection { get; } = new[]
    {
        new FieldDefinition { Key = DrawingKeys.EbsId,        DisplayName = "EBS-ID",   Source = FieldSource.Ebs, Order = 1, IsRowKey = true },
        new FieldDefinition { Key = DrawingKeys.InvOrg,       DisplayName = "库存组织", Source = FieldSource.Ebs, Order = 2 },
        new FieldDefinition { Key = DrawingKeys.SourceNo,     DisplayName = "来源单号", Source = FieldSource.Ebs, Order = 3 },
        new FieldDefinition { Key = DrawingKeys.Project,      DisplayName = "项目",     Source = FieldSource.Ebs, Order = 4 },
        new FieldDefinition { Key = DrawingKeys.ProductLine,  DisplayName = "产品线",   Source = FieldSource.Ebs, Order = 5 },
        new FieldDefinition { Key = DrawingKeys.PlanNo,       DisplayName = "方案编号", Source = FieldSource.Ebs, Order = 6 },
        new FieldDefinition { Key = DrawingKeys.DeptDesc,     DisplayName = "部门描述", Source = FieldSource.Ebs, Order = 7 },
        new FieldDefinition { Key = DrawingKeys.MaterialCode, DisplayName = "物料编码", Source = FieldSource.Ebs, Order = 8 },
        new FieldDefinition { Key = DrawingKeys.MaterialDesc, DisplayName = "物料描述", Source = FieldSource.Ebs, Order = 9 },
        new FieldDefinition { Key = DrawingKeys.CurrentQty,   DisplayName = "当前数量", Source = FieldSource.Ebs, Order = 10 },
        new FieldDefinition { Key = DrawingKeys.CreateDate,   DisplayName = "创建日期", Source = FieldSource.Ebs, Order = 11 },
        new FieldDefinition { Key = DrawingKeys.DemandDate,   DisplayName = "需求日期", Source = FieldSource.Ebs, Order = 12 },
        new FieldDefinition { Key = DrawingKeys.Applicant,    DisplayName = "申请人名称", Source = FieldSource.Ebs, Order = 13 },
        new FieldDefinition { Key = DrawingKeys.Remark,       DisplayName = "备注",     Source = FieldSource.Ebs, Order = 14 },
        new FieldDefinition { Key = DrawingKeys.HasChange,    DisplayName = "是否存在变更", Source = FieldSource.Plm, Order = 15 },
        new FieldDefinition { Key = DrawingKeys.CanMachine,   DisplayName = "是否机加中心可以做", Source = FieldSource.Manual, Editor = FieldEditor.Dropdown, Options = new[] { "是", "否" }, IsRequired = true, Order = 16 },
    };

    public static IReadOnlyList<FieldDefinition> For(FlowType flow) =>
        flow == FlowType.Pricing ? Pricing : DrawingSelection;

    /// <summary>取行标识键字段。</summary>
    public static FieldDefinition RowKeyField(FlowType flow) =>
        For(flow).First(f => f.IsRowKey);

    /// <summary>取待填列（手填字段）。</summary>
    public static IEnumerable<FieldDefinition> ManualFields(FlowType flow) =>
        For(flow).Where(f => f.IsEditable);
}
