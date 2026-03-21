using System;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace ManosabaLoader.Utils
{
    /// <summary>
    /// 通过 IL2CPP raw API 访问对象字段（包括泛型基类中的字段）。
    /// 遍历类继承链查找字段名，使用 il2cpp_field_get_offset 定位。
    /// </summary>
    static class Il2CppFieldHelper
    {
        public static Action<string> FieldHelperLogMessage;
        public static Action<string> FieldHelperLogInfo;
        public static Action<string> FieldHelperLogDebug;
        public static Action<string> FieldHelperLogWarning;
        public static Action<string> FieldHelperLogError;

        /// <summary>按字段名读取引用类型字段（遍历继承链查找）。</summary>
        public static unsafe IntPtr GetReferenceField(Il2CppObjectBase obj, string fieldName)
        {
            if (obj == null) return IntPtr.Zero;
            return GetReferenceField(obj.Pointer, fieldName);
        }

        /// <summary>按字段名从原始 IL2CPP 对象指针读取引用类型字段。</summary>
        public static unsafe IntPtr GetReferenceField(IntPtr objPtr, string fieldName)
        {
            if (objPtr == IntPtr.Zero) return IntPtr.Zero;

            IntPtr current = IL2CPP.il2cpp_object_get_class(objPtr);
            while (current != IntPtr.Zero)
            {
                IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(current, fieldName);
                if (field != IntPtr.Zero)
                {
                    int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
                    return *(IntPtr*)(objPtr + offset);
                }
                current = IL2CPP.il2cpp_class_get_parent(current);
            }

            FieldHelperLogWarning($"Field '{fieldName}' not found in hierarchy.");
            return IntPtr.Zero;
        }

        /// <summary>
        /// 动态解析嵌入在类中的值类型（struct）内的引用字段。
        /// 先在 obj 的类中找到 structFieldName（值类型字段）获取其偏移，
        /// 再在该值类型中找到 innerFieldName（引用字段）获取其偏移，
        /// 返回 obj.Pointer + structOffset + innerOffset 处的 IntPtr。
        /// </summary>
        public static unsafe IntPtr GetNestedReferenceField(
            Il2CppObjectBase obj, string structFieldName, string innerFieldName)
        {
            if (obj == null) return IntPtr.Zero;

            IntPtr current = IL2CPP.il2cpp_object_get_class(obj.Pointer);
            IntPtr structField = IntPtr.Zero;

            while (current != IntPtr.Zero)
            {
                structField = IL2CPP.il2cpp_class_get_field_from_name(current, structFieldName);
                if (structField != IntPtr.Zero) break;
                current = IL2CPP.il2cpp_class_get_parent(current);
            }

            if (structField == IntPtr.Zero)
            {
                FieldHelperLogWarning($"Struct field '{structFieldName}' not found in hierarchy.");
                return IntPtr.Zero;
            }

            int structOffset = (int)IL2CPP.il2cpp_field_get_offset(structField);

            // 获取值类型的 IL2CPP class 以查找内部字段
            IntPtr fieldType = IL2CPP.il2cpp_field_get_type(structField);
            IntPtr structClass = IL2CPP.il2cpp_class_from_type(fieldType);
            if (structClass == IntPtr.Zero)
            {
                FieldHelperLogWarning($"Could not resolve class for struct field '{structFieldName}'.");
                return IntPtr.Zero;
            }

            IntPtr innerField = IL2CPP.il2cpp_class_get_field_from_name(structClass, innerFieldName);
            if (innerField == IntPtr.Zero)
            {
                FieldHelperLogWarning($"Inner field '{innerFieldName}' not found in struct '{structFieldName}'.");
                return IntPtr.Zero;
            }

            // 值类型字段的 il2cpp_field_get_offset 返回包含 Il2CppObject 头偏移
            // （用于 boxed 表示），嵌入在类中时需减去对象头大小（2 × 指针宽度）
            int innerOffset = (int)IL2CPP.il2cpp_field_get_offset(innerField) - 2 * IntPtr.Size;
            if (innerOffset < 0) innerOffset = 0;

            return *(IntPtr*)(obj.Pointer + structOffset + innerOffset);
        }

        /// <summary>按字段名写入引用类型字段（遍历继承链查找）。</summary>
        public static unsafe void SetReferenceField(Il2CppObjectBase obj, string fieldName, IntPtr value)
        {
            if (obj == null) return;

            IntPtr current = IL2CPP.il2cpp_object_get_class(obj.Pointer);
            while (current != IntPtr.Zero)
            {
                IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(current, fieldName);
                if (field != IntPtr.Zero)
                {
                    int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
                    *(IntPtr*)(obj.Pointer + offset) = value;
                    return;
                }
                current = IL2CPP.il2cpp_class_get_parent(current);
            }

            FieldHelperLogWarning($"Field '{fieldName}' not found, cannot write.");
        }

        /// <summary>按已知偏移量直接读取引用字段（跳过字段名查找，更快）。</summary>
        public static unsafe IntPtr ReadByOffset(Il2CppObjectBase obj, int offset)
        {
            if (obj == null) return IntPtr.Zero;
            return *(IntPtr*)(obj.Pointer + offset);
        }

        /// <summary>按字段名读取值类型字段（int）。用于读取 VersionedItem._version 等。</summary>
        public static unsafe int GetIntField(Il2CppObjectBase obj, string fieldName, int fallback = 0)
        {
            if (obj == null) return fallback;

            IntPtr current = IL2CPP.il2cpp_object_get_class(obj.Pointer);
            while (current != IntPtr.Zero)
            {
                IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(current, fieldName);
                if (field != IntPtr.Zero)
                {
                    int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
                    return *(int*)(obj.Pointer + offset);
                }
                current = IL2CPP.il2cpp_class_get_parent(current);
            }

            FieldHelperLogWarning($"Int field '{fieldName}' not found in hierarchy.");
            return fallback;
        }
    }
}
