using System;
using System.Collections.Generic;
using System.IO;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Editing;
using Jun.Cad.Batch.Engine;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 配置加载与类型级校验的**命令行输出形态**。
    ///
    /// 校验实现只有一份（`RuleValidator`），`ConfigLoader` 只负责翻译成
    /// `规则「名」原因` 这样一行一条。这里的断言盯的是那个形状——
    /// 有人把前缀改掉、或者把校验又抄回 ConfigLoader 一份，测试会红。
    /// </summary>
    public class ConfigLoaderTests
    {
        private static string WriteConfig(string json)
        {
            string dir = TestPaths.NewTempDirectory("config");
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void 缺必填字段时给出一行一条的提示()
        {
            string path = WriteConfig(@"{
  ""Version"": 1,
  ""Rules"": [
    { ""Name"": ""改层"", ""Type"": ""EntityLayerChange"",
      ""Filter"": { ""EntityTypes"": [""LINE""] }, ""Action"": {} }
  ]
}");

            List<string> warnings;
            ConfigLoader.Load(path, out warnings);

            Assert.Contains(warnings, w => w == "规则「改层」缺少目标图层（Action.TargetLayer）。");
        }

        /// <summary>
        /// 合并前只有 9 种类型会被校验，`TextReplace` 的非法正则在命令行是**静默**的。
        /// 现在必须在加载阶段就说出来。
        /// </summary>
        [Fact]
        public void 原来不校验的类型现在也会提示()
        {
            string path = WriteConfig(@"{
  ""Version"": 1,
  ""Rules"": [
    { ""Name"": ""替换"", ""Type"": ""TextReplace"",
      ""Filter"": { ""EntityTypes"": [""TEXT""] },
      ""Action"": { ""MatchMode"": ""Regex"", ""SearchText"": ""("", ""Replacement"": ""x"" } }
  ]
}");

            List<string> warnings;
            ConfigLoader.Load(path, out warnings);

            Assert.Contains(warnings, w => w.StartsWith("规则「替换」") && w.Contains("正则表达式无效"));
        }

        [Fact]
        public void 写操作规则没有过滤条件时提示会中止()
        {
            string path = WriteConfig(@"{
  ""Version"": 1,
  ""Rules"": [
    { ""Name"": ""全图改层"", ""Type"": ""EntityLayerChange"", ""Filter"": {}, ""Action"": { ""TargetLayer"": ""X"" } }
  ]
}");

            List<string> warnings;
            ConfigLoader.Load(path, out warnings);

            Assert.Contains(warnings, w => w.Contains("没有任何过滤条件"));
        }

        [Fact]
        public void 只配空间条件不报没有过滤条件()
        {
            string path = WriteConfig(@"{
  ""Version"": 1,
  ""Rules"": [
    { ""Name"": ""图框内改层"", ""Type"": ""EntityLayerChange"",
      ""Filter"": { ""ContainerBlockPattern"": ""TB-*"" }, ""Action"": { ""TargetLayer"": ""X"" } }
  ]
}");

            List<string> warnings;
            ConfigLoader.Load(path, out warnings);

            Assert.DoesNotContain(warnings, w => w.Contains("没有任何过滤条件"));
        }

        [Fact]
        public void 未注册的类型给出提示但不阻断加载()
        {
            string path = WriteConfig(@"{
  ""Version"": 1,
  ""Rules"": [ { ""Name"": ""x"", ""Type"": ""NoSuchRule"", ""Filter"": {}, ""Action"": {} } ]
}");

            List<string> warnings;
            BatchConfig config = ConfigLoader.Load(path, out warnings);

            Assert.Single(config.Rules);
            Assert.Contains(warnings, w => w.Contains("未注册"));
        }

        [Fact]
        public void 未知字段被原样保留()
        {
            string path = WriteConfig(@"{
  ""Version"": 1,
  ""Rules"": [
    { ""Name"": ""r"", ""Type"": ""SetByLayer"",
      ""Filter"": { ""EntityTypes"": [""LINE""], ""MyOwnFlag"": true },
      ""Action"": { ""ForceColorByLayer"": false, ""Custom"": ""值"" } }
  ]
}");

            List<string> warnings;
            BatchConfig config = ConfigLoader.Load(path, out warnings);

            RuleConfig rule = config.Rules[0];
            Assert.True(rule.Filter.Extra.ContainsKey("MyOwnFlag"));
            Assert.True(rule.Action.Extra.ContainsKey("Custom"));
        }

        [Fact]
        public void 没有规则时给出提示()
        {
            string path = WriteConfig(@"{ ""Version"": 1, ""Rules"": [] }");

            List<string> warnings;
            ConfigLoader.Load(path, out warnings);

            Assert.Contains(warnings, w => w.Contains("没有任何规则"));
        }

        [Fact]
        public void 规则名缺失时自动补一个()
        {
            string path = WriteConfig(@"{
  ""Version"": 1,
  ""Rules"": [ { ""Type"": ""SetByLayer"", ""Filter"": { ""EntityTypes"": [""LINE""] }, ""Action"": {} } ]
}");

            List<string> warnings;
            BatchConfig config = ConfigLoader.Load(path, out warnings);

            Assert.False(string.IsNullOrWhiteSpace(config.Rules[0].Name));
        }

        // ------------------------------------------------------------ 默认配置

        [Fact]
        public void 默认配置文件名是Jun点Cad点Batch点json()
        {
            Assert.Equal("Jun.Cad.Batch.json", ConfigLoader.DefaultFileName);
        }

        /// <summary>默认路径必须是 exe 同目录——单文件发布时 AppContext.BaseDirectory 指向解压目录，不能用它。</summary>
        [Fact]
        public void 默认配置路径落在程序所在目录()
        {
            string path = ConfigLoader.GetDefaultConfigPath();

            Assert.False(string.IsNullOrWhiteSpace(path));
            Assert.Equal(ConfigLoader.DefaultFileName, Path.GetFileName(path));

            string expectedDir = ConfigLoader.GetExecutableDirectory();
            Assert.False(string.IsNullOrWhiteSpace(expectedDir));
            Assert.Equal(Path.GetFullPath(expectedDir), Path.GetFullPath(Path.GetDirectoryName(path)));
        }

        /// <summary>
        /// 内置模板必须"开箱可用"：覆盖全部已注册规则类型、默认全部关闭、
        /// 且加载时**不报任何问题**。
        ///
        /// 最后一条最容易被写漏：模板里某条写操作规则忘了给 Filter，
        /// 用户第一次运行就会看到"没有任何过滤条件，执行时会中止"的告警——
        /// 明明是默认配置的问题，却像是用户配错了。
        /// </summary>
        [Fact]
        public void 内置默认模板覆盖全部规则类型且默认关闭()
        {
            string json = ConfigLoader.LoadTemplate();
            Assert.False(string.IsNullOrWhiteSpace(json));

            string path = WriteConfig(json);
            List<string> warnings;
            BatchConfig config = ConfigLoader.Load(path, out warnings);

            Assert.Empty(warnings);
            Assert.Equal(20, config.Rules.Count);
            Assert.All(config.Rules, r => Assert.False(r.Enabled));

            var expected = new HashSet<string>(RuleFactory.GetRegisteredTypes(), StringComparer.OrdinalIgnoreCase);
            var actual = new HashSet<string>(
                config.Rules.ConvertAll(r => (r.Type ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);

            Assert.Equal(expected.Count, actual.Count);
            Assert.True(expected.SetEquals(actual),
                "模板缺少规则类型：" + string.Join(",", new List<string>(expected).FindAll(t => !actual.Contains(t))));
        }
    }
}
