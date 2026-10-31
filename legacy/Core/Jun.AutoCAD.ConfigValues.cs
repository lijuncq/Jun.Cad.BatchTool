namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 配置值的默认值取值工具。
    ///
    /// 为什么单独一个类：这些方法原来挂在 <see cref="RuleExecutionContext"/> 上，而那个类的字段类型是
    /// <c>Document</c> / <c>Transaction</c> / <c>Editor</c>。任何调用方（例如 <see cref="RuleSummary"/>）
    /// 一旦碰到它，测试进程就会连带加载 acdbmgd，而 AutoCAD 托管程序集在宿主进程外无法加载。
    /// 把纯函数搬到这里之后，摘要、校验这些"只读配置"的逻辑才能被单元测试覆盖。
    ///
    /// 语义不变：null 一律取调用方给的默认值。
    /// </summary>
    public static class ConfigValues
    {
        public static bool Bool(bool? value, bool defaultValue)
        {
            return value.HasValue ? value.Value : defaultValue;
        }

        public static double Double(double? value, double defaultValue)
        {
            return value.HasValue ? value.Value : defaultValue;
        }

        public static int Int(int? value, int defaultValue)
        {
            return value.HasValue ? value.Value : defaultValue;
        }
    }
}
