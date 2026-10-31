using System;

namespace Jun.Cad.Batch.Editing
{
    /// <summary>可对照符号表校验的名称种类。</summary>
    public enum NameKind
    {
        Layer,
        Block,
        TextStyle,
        DimStyle,
        Linetype
    }

    /// <summary>
    /// 一次名称查询的结果。
    ///
    /// 三态设计的原因：UI 必须能区分"查过了，确实没有"和"查不了"。
    /// 查不了（没有打开文档、读表异常、锁冲突）时绝不能提示"名字不存在"，
    /// 否则用户会在没有图纸的场景下看到一片假警告。
    /// </summary>
    public sealed class NameLookupResult
    {
        public NameLookupResult(bool known, bool found, string nearest)
        {
            Known = known;
            Found = found;
            Nearest = nearest;
        }

        /// <summary>false 表示查不了（无文档 / 读失败 / 锁冲突），调用方应静默跳过。</summary>
        public bool Known { get; private set; }

        /// <summary>名称是否存在。</summary>
        public bool Found { get; private set; }

        /// <summary>最接近的候选（找不到或没有相近项时为 null）。</summary>
        public string Nearest { get; private set; }

        public static NameLookupResult Unknown()
        {
            return new NameLookupResult(false, false, null);
        }

        public static NameLookupResult Exists()
        {
            return new NameLookupResult(true, true, null);
        }

        public static NameLookupResult Missing(string nearest)
        {
            return new NameLookupResult(true, false, nearest);
        }
    }

    /// <summary>
    /// 名称查询接口。
    ///
    /// 存在的唯一理由：保存时要对照当前图纸的符号表校验图层/块/样式名，而
    /// <c>LayerService</c> / <c>BlockNameUtil</c> 的签名直接吃 <c>Transaction</c> / <c>Database</c>，
    /// 让 UI 调它们就等于 UI 引用了宿主类型，破坏"UI 层不依赖 AutoCAD/ZWCAD 类型"的约束。
    /// 因此把宿主推到 <see cref="HostNameLookup"/> 里，UI 只认这个接口。
    /// </summary>
    public interface INameLookup
    {
        /// <summary>当前图纸的显示名，用于把提示写清楚"查的是哪张图"。</summary>
        string ContextName { get; }

        /// <summary>查询名称是否存在；查不了时返回 <see cref="NameLookupResult.Unknown"/>。</summary>
        NameLookupResult Lookup(NameKind kind, string name);
    }
}
