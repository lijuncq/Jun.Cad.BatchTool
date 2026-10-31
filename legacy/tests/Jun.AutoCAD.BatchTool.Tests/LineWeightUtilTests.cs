using System.Collections.Generic;
using System.Reflection;
using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;
using Xunit;

namespace Jun.AutoCAD.BatchTool.Tests
{
    /// <summary>
    /// 线宽取值的回归测试。
    ///
    /// 背景（2026-10-29 实机事故）：配置里写 <c>"LayerLineWeight": 1</c>，
    /// <c>record.LineWeight = (LineWeight)1</c> 在 AutoCAD 里抛 <c>eInvalidInput</c>，
    /// 整条 BlockLayerChange 规则以"匹配 0 / 成功 0 / 规则中止"结束。
    /// 1 在 -3~211 的"数值范围"里是合法的，但在 <c>LineWeight</c> **枚举**里不存在——
    /// 所以校验必须是"是不是枚举值"，不能是"在不在范围内"。
    /// </summary>
    public class LineWeightUtilTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(24)]
        [InlineData(26)]
        [InlineData(212)]
        [InlineData(-5)]
        public void 枚举里不存在的值一律非法(int value)
        {
            Assert.False(LineWeightUtil.IsValidEntityValue(value));
            Assert.False(LineWeightUtil.IsValidLayerValue(value));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        [InlineData(25)]
        [InlineData(211)]
        public void 具体线宽合法(int value)
        {
            Assert.True(LineWeightUtil.IsValidEntityValue(value));
            Assert.True(LineWeightUtil.IsValidLayerValue(value));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-2)]
        [InlineData(-3)]
        public void 符号线宽只能用于实体不能用于图层(int value)
        {
            Assert.True(LineWeightUtil.IsValidEntityValue(value));
            Assert.False(LineWeightUtil.IsValidLayerValue(value));
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(23, 25)]
        [InlineData(26, 25)]
        [InlineData(-1, 0)]
        [InlineData(999, 211)]
        public void 最接近的合法图层线宽(int input, int expected)
        {
            Assert.Equal(expected, LineWeightUtil.NearestLayerValue(input));
        }

        [Fact]
        public void 描述文字()
        {
            Assert.Equal("0.25mm", LineWeightUtil.Describe(25));
            Assert.Equal("0.00mm", LineWeightUtil.Describe(0));
            Assert.Contains("ByLayer", LineWeightUtil.Describe(-1));
        }

        /// <summary>
        /// 字段候选串是写在特性里的常量（编译期字符串），没法直接引用 LineWeightUtil，
        /// 所以这里断言两边一致：以后改合法值集合忘了同步特性，这条测试会红。
        /// </summary>
        [Fact]
        public void 表单候选串与合法值集合一致()
        {
            AssertOptionKeys("LayerLineWeight", LineWeightUtil.LayerValues);
            AssertOptionKeys("LineWeight", LineWeightUtil.EntityValues);
        }

        [Fact]
        public void 线宽字段用下拉而不是自由输入()
        {
            Assert.Equal(RuleFieldEditor.Enum, Meta("LayerLineWeight").Editor);
            Assert.Equal(RuleFieldEditor.Enum, Meta("LineWeight").Editor);
        }

        private static RuleFieldAttribute Meta(string propertyName)
        {
            PropertyInfo property = typeof(RuleAction).GetProperty(propertyName);
            Assert.NotNull(property);
            return property.GetCustomAttribute<RuleFieldAttribute>();
        }

        private static void AssertOptionKeys(string propertyName, int[] expected)
        {
            RuleFieldAttribute meta = Meta(propertyName);
            Assert.False(string.IsNullOrEmpty(meta.Values));

            var keys = new List<int>();
            foreach (string part in meta.Values.Split(';'))
            {
                string[] pair = part.Split('=');
                Assert.Equal(2, pair.Length);
                keys.Add(int.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture));
            }

            // 下拉里的排列顺序是显示选择（随层/随块/默认排在前面），这里只比集合
            int[] actual = keys.ToArray();
            System.Array.Sort(actual);
            int[] expectedSorted = (int[])expected.Clone();
            System.Array.Sort(expectedSorted);
            Assert.Equal(expectedSorted, actual);
            Assert.Equal(keys.Count, actual.Length);
        }
    }
}
