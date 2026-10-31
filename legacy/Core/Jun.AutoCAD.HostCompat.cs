using System;
using System.Reflection;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 宿主差异收口点。
    ///
    /// 本插件同时支持 AutoCAD（Autodesk.AutoCAD.*）与中望CAD 2020（ZwSoft.ZwCAD.*）两套托管 API，
    /// 两者的类型形状几乎一一对应，但仍有少量差异，集中在这里处理，避免把 <c>#if ZWCAD</c>
    /// 散落到业务代码里：
    ///   * 产品名（写进日志/自检信息，避免在 ZWCAD 里显示"AutoCAD"）；
    ///   * 宿主版本号；
    ///   * 无文档时能否弹对话框（两个宿主的 Application 入口不同）。
    ///
    /// 注意：这里只放"取信息 / 兜底"这类薄封装，真正的数据库操作仍然直接写 ZwSoft./Autodesk. 命名空间。
    /// </summary>
    public static class HostCompat
    {
        /// <summary>宿主产品名："AutoCAD" 或 "中望CAD"。</summary>
        public static string ProductName
        {
            get
            {
#if ZWCAD
                return "中望CAD";
#else
                return "AutoCAD";
#endif
            }
        }

        /// <summary>宿主产品的完整标识（写日志用）。</summary>
        public static string Describe
        {
            get { return ProductName + " " + HostVersion; }
        }

        /// <summary>宿主文档集合（AutoCAD 与 ZWCAD 同名同形状）。读取失败返回 null。</summary>
        public static DocumentCollection DocumentManager
        {
            get
            {
                try
                {
                    return Application.DocumentManager;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>宿主版本号字符串；读取失败返回 "(未知)"。</summary>
        public static string HostVersion
        {
            get
            {
                try
                {
                    return Application.Version.ToString();
                }
                catch
                {
                    return "(未知)";
                }
            }
        }

        /// <summary>兼容标记：当前编译目标是中望CAD。</summary>
        public static bool IsZwCad
        {
            get
            {
#if ZWCAD
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// 宿主支持的"随层/随块"线型名判定差异（ZWCAD 2020 的 LineWeight 枚举与 AutoCAD 一致，
        /// 这里保留一个统一入口，便于将来出现差异时只改一处）。
        /// </summary>
        public static bool IsSymbolicLinetype(string name)
        {
            return string.Equals(name, "ByLayer", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "ByBlock", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 反射设置属性（用于两套 API 属性形状不同、且失败可忽略的场景，如 Cell.TextStyleId）。
        /// </summary>
        public static bool TrySetProperty(object target, string propertyName, object value, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "对象为空";
                return false;
            }

            try
            {
                PropertyInfo prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite)
                {
                    reason = string.Format("{0} 不支持 {1}", target.GetType().Name, propertyName);
                    return false;
                }

                prop.SetValue(target, value, null);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }
    }
}
