using System;
using System.Collections.Generic;
using Jun.Cad.Batch.Editing;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 假的名称查询：不读任何符号表，只按预置集合回答。
    /// 用它验证 <see cref="NameValidator"/> 的三态处理（Known / Found / Nearest / Missing），
    /// 不需要 DWG 文件。
    /// </summary>
    internal sealed class FakeNameLookup : INameLookup
    {
        private readonly Dictionary<NameKind, List<string>> _names = new Dictionary<NameKind, List<string>>();

        public FakeNameLookup(string contextName)
        {
            ContextName = contextName ?? "test.dwg";
        }

        public string ContextName { get; private set; }

        /// <summary>为 true 时所有查询都返回 Unknown，模拟"读不了图纸"。</summary>
        public bool AlwaysUnknown { get; set; }

        public FakeNameLookup With(NameKind kind, params string[] names)
        {
            List<string> list;
            if (!_names.TryGetValue(kind, out list))
            {
                list = new List<string>();
                _names[kind] = list;
            }

            list.AddRange(names);
            return this;
        }

        public NameLookupResult Lookup(NameKind kind, string name)
        {
            if (AlwaysUnknown)
            {
                return NameLookupResult.Unknown();
            }

            List<string> list;
            if (!_names.TryGetValue(kind, out list))
            {
                return NameLookupResult.Unknown();
            }

            foreach (string candidate in list)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    return NameLookupResult.Exists();
                }
            }

            return NameLookupResult.Missing(NameMatcher.Nearest(name, list));
        }
    }
}
