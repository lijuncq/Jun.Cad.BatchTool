using System;
using System.Collections.Generic;
using System.IO;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// <see cref="INameLookup"/> 的宿主实现——全项目**唯一**读取符号表做名称校验的地方。
    ///
    /// 三个约束：
    ///   1. **拿文档锁再读**。界面是非模式窗口，没有命令上下文，直接开事务读表可能撞上
    ///      <c>eLockViolation</c>；
    ///   2. **读一次就缓存**。一张图里图层/块动辄上千，12 条规则逐条查会把符号表翻十几遍；
    ///   3. **任何异常都返回 <c>Known=false</c>**。校验不能成为保存失败的原因，
    ///      也不能因为读不到就报"名字不存在"。
    ///
    /// 文档切换后缓存作废：`Lookup` 先比较当前活动文档是否还是构造时那一个，
    /// 不是就直接返回 Unknown，避免用旧图的候选值去校验新图的配置。
    /// </summary>
    public sealed class HostNameLookup : INameLookup
    {
        private readonly Document _document;
        private readonly Dictionary<NameKind, List<string>> _cache = new Dictionary<NameKind, List<string>>();
        private readonly object _sync = new object();

        public HostNameLookup(Document document)
        {
            _document = document;
        }

        public string ContextName
        {
            get
            {
                try
                {
                    string name = _document == null ? null : _document.Name;
                    return string.IsNullOrEmpty(name) ? "(未命名图形)" : Path.GetFileName(name);
                }
                catch
                {
                    return "(未命名图形)";
                }
            }
        }

        public NameLookupResult Lookup(NameKind kind, string name)
        {
            List<string> names = GetNames(kind);
            if (names == null)
            {
                return NameLookupResult.Unknown();
            }

            foreach (string candidate in names)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    return NameLookupResult.Exists();
                }
            }

            return NameLookupResult.Missing(NameMatcher.Nearest(name, names));
        }

        private List<string> GetNames(NameKind kind)
        {
            lock (_sync)
            {
                List<string> cached;
                if (_cache.TryGetValue(kind, out cached))
                {
                    return cached;
                }

                List<string> loaded = LoadNames(kind);
                if (loaded == null)
                {
                    return null;
                }

                _cache[kind] = loaded;
                return loaded;
            }
        }

        private List<string> LoadNames(NameKind kind)
        {
            if (_document == null)
            {
                return null;
            }

            // 切了图纸就别用旧缓存，也别拿新图的表去回答（实例是跟着窗口创建的）
            try
            {
                if (Application.DocumentManager.MdiActiveDocument != _document)
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }

            try
            {
                Database db = _document.Database;
                var names = new List<string>();

                using (_document.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    switch (kind)
                    {
                        case NameKind.Layer:
                            Collect(tr, db.LayerTableId, names, false);
                            break;

                        case NameKind.Block:
                            // 匿名块（*U12 这类动态块实例、*D 标注块）对用户没有意义，不参与候选
                            Collect(tr, db.BlockTableId, names, true);
                            break;

                        case NameKind.TextStyle:
                            Collect(tr, db.TextStyleTableId, names, false);
                            break;

                        case NameKind.DimStyle:
                            Collect(tr, db.DimStyleTableId, names, false);
                            break;

                        case NameKind.Linetype:
                            Collect(tr, db.LinetypeTableId, names, false);
                            break;

                        default:
                            return null;
                    }
                }

                return names;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(string.Format(
                    "HostNameLookup 读取 {0} 表失败（{1}）：{2}", kind, ex.GetType().Name, ex.Message));
                return null;
            }
        }

        /// <summary>把一张符号表里的名字收集出来。</summary>
        private static void Collect(Transaction tr, ObjectId tableId, List<string> names, bool skipAnonymous)
        {
            var table = tr.GetObject(tableId, OpenMode.ForRead, false) as SymbolTable;
            if (table == null)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                SymbolTableRecord record;
                try
                {
                    record = tr.GetObject(id, OpenMode.ForRead, false) as SymbolTableRecord;
                }
                catch
                {
                    continue;
                }

                if (record == null)
                {
                    continue;
                }

                string name;
                try
                {
                    name = record.Name;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (skipAnonymous && name[0] == '*')
                {
                    continue;
                }

                names.Add(name);
            }
        }
    }
}
