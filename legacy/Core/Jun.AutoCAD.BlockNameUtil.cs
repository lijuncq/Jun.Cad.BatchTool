using System;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 块名解析工具。
    ///
    /// 动态块的一个实例在块表中是匿名块（*U12 之类），
    /// 直接用 BlockReference.Name 拿到的是匿名名，无法与配置里的源块名比较；
    /// 因此这里统一取"有效块名"：动态块取 DynamicBlockTableRecord（原始动态块定义）的名字。
    /// </summary>
    public static class BlockNameUtil
    {
        /// <summary>
        /// 取块参照的有效块名（动态块返回动态块定义名，普通块返回块名）。
        /// </summary>
        public static string GetEffectiveName(BlockReference br, Transaction tr)
        {
            if (br == null)
            {
                return null;
            }

            string name = null;
            try
            {
                name = br.Name;
            }
            catch
            {
                // 极端情况下（对象已删除）忽略
            }

            try
            {
                if (br.IsDynamicBlock && tr != null)
                {
                    var btr = tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                    if (btr != null && !string.IsNullOrEmpty(btr.Name))
                    {
                        return btr.Name;
                    }
                }
            }
            catch
            {
                // 保持回退到 BlockReference.Name
            }

            return name;
        }

        /// <summary>块参照是否匹配指定块名（同时比较匿名名与有效块名，大小写不敏感）。</summary>
        public static bool Matches(BlockReference br, string blockName, Transaction tr)
        {
            if (br == null || string.IsNullOrWhiteSpace(blockName))
            {
                return false;
            }

            string target = blockName.Trim();

            string raw = null;
            try
            {
                raw = br.Name;
            }
            catch
            {
                // ignore
            }

            if (string.Equals(raw, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string effective = GetEffectiveName(br, tr);
            return string.Equals(effective, target, StringComparison.OrdinalIgnoreCase);
        }
    }
}
