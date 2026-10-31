using System;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 日志/错误信息中描述对象的小工具：统一输出"类型(句柄:XXXX) 图层:YYY"，
    /// 便于用户按句柄在图纸里定位失败对象。
    /// </summary>
    public static class ObjectUtil
    {
        public static string HandleOf(ObjectId id)
        {
            try
            {
                return id.IsNull ? "null" : id.Handle.ToString();
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>描述一个对象；对象已打开或需要只读打开时都能用。</summary>
        public static string Describe(Transaction tr, ObjectId id)
        {
            string handle = HandleOf(id);
            try
            {
                var obj = tr.GetObject(id, OpenMode.ForRead, false);
                if (obj == null)
                {
                    return string.Format("(句柄:{0})", handle);
                }

                var ent = obj as Entity;
                if (ent != null)
                {
                    return string.Format("{0}(句柄:{1}) 图层:{2}", ent.GetType().Name, handle, ent.Layer);
                }

                var dbObj = obj as DBObject;
                string name = dbObj != null ? dbObj.GetType().Name : obj.GetType().Name;
                return string.Format("{0}(句柄:{1})", name, handle);
            }
            catch (Exception ex)
            {
                return string.Format("(句柄:{0}, 读取失败:{1})", handle, ex.Message);
            }
        }

        /// <summary>描述一个已经打开的实体（不再走事务打开）。</summary>
        public static string Describe(Entity ent)
        {
            if (ent == null)
            {
                return "(null)";
            }

            string handle = "?";
            try
            {
                handle = ent.Handle.ToString();
            }
            catch
            {
                // ignore
            }

            return string.Format("{0}(句柄:{1}) 图层:{2}", ent.GetType().Name, handle, ent.Layer);
        }
    }
}
