using System;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using BF = Il2CppSystem.Reflection.BindingFlags;
using Il2CppFieldInfo = Il2CppSystem.Reflection.FieldInfo;
using Il2CppType_ = Il2CppSystem.Type;

namespace Dolophobia.Game;

/// <summary>
/// A field whose name is obfuscated (Phasmophobia renames private members to random
/// Malayalam strings, re-rolled every game update). We locate it by shape instead:
/// "the only LevelRoom field on GhostInfo", "the first public float on PlayerSanity", etc.
/// and then read it directly at its native offset, exactly like interop-generated accessors do.
/// </summary>
internal sealed class ObfField
{
    public readonly string Description;
    public readonly int Offset;

    public ObfField(string description, int offset)
    {
        Description = description;
        Offset = offset;
    }

    public unsafe T Read<T>(Il2CppObjectBase obj) where T : unmanaged =>
        *(T*)((byte*)obj.Pointer + Offset);

    // Only safe for unmanaged values; writing object references would skip the GC write barrier.
    public unsafe void Write<T>(Il2CppObjectBase obj, T value) where T : unmanaged =>
        *(T*)((byte*)obj.Pointer + Offset) = value;

    public unsafe T ReadRef<T>(Il2CppObjectBase obj) where T : Il2CppObjectBase
    {
        var ptr = *(IntPtr*)((byte*)obj.Pointer + Offset);
        return ptr == IntPtr.Zero ? null : (T)Activator.CreateInstance(typeof(T), ptr);
    }
}

internal static class Obf
{
    const BF Declared = BF.Instance | BF.Public | BF.NonPublic | BF.DeclaredOnly;

    // Boxed value-type field offsets include the 16-byte object header; inline struct members don't.
    const int ObjectHeaderSize = 0x10;

    public static bool Is<T>(Il2CppType_ type) where T : Il2CppObjectBase =>
        type.FullName == Il2CppType.Of<T>().FullName;

    public static bool IsEnumWith(Il2CppType_ type, string valueName) =>
        type.IsEnum && type.GetEnumNames().Contains(valueName);

    public static ObfField Find<TOwner>(string description, Func<Il2CppFieldInfo, bool> match, int nth = 0)
        where TOwner : Il2CppObjectBase =>
        Find(Il2CppType.Of<TOwner>(), description, match, nth);

    public static ObfField Find(Il2CppType_ owner, string description, Func<Il2CppFieldInfo, bool> match, int nth = 0)
    {
        var field = FindInfo(owner, match, nth);
        if (field == null)
        {
            Plugin.Logger.LogWarning($"[Obf] Could not resolve {description} on {owner.FullName}. Game update?");
            return null;
        }
        var resolved = new ObfField(description, OffsetOf(owner, field));
        Plugin.Logger.LogDebug($"[Obf] {description} -> {owner.Name}.{field.Name} @0x{resolved.Offset:X}");
        return resolved;
    }

    /// <summary>A member of a struct that is stored inline inside an object, e.g. GhostInfo.traits.ghostType.</summary>
    public static ObfField FindNested<TOwner>(string description,
        Func<Il2CppFieldInfo, bool> matchStruct, Func<Il2CppFieldInfo, bool> matchMember)
        where TOwner : Il2CppObjectBase
    {
        var owner = Il2CppType.Of<TOwner>();
        var outer = FindInfo(owner, f => f.FieldType.IsValueType && !f.FieldType.IsEnum && matchStruct(f), 0);
        var inner = outer == null ? null : FindInfo(outer.FieldType, matchMember, 0);
        if (inner == null)
        {
            Plugin.Logger.LogWarning($"[Obf] Could not resolve {description} on {owner.FullName}. Game update?");
            return null;
        }
        var offset = OffsetOf(owner, outer) + OffsetOf(outer.FieldType, inner) - ObjectHeaderSize;
        return new ObfField(description, offset);
    }

    /// <summary>Value names of the first enum field on TOwner that contains <paramref name="valueName"/>.</summary>
    public static string[] EnumNamesWith<TOwner>(string valueName) where TOwner : Il2CppObjectBase
    {
        var info = FindInfo(Il2CppType.Of<TOwner>(), f => IsEnumWith(f.FieldType, valueName), 0);
        return info?.FieldType.GetEnumNames().ToArray() ?? Array.Empty<string>();
    }

    internal static Il2CppFieldInfo FindInfo(Il2CppType_ owner, Func<Il2CppFieldInfo, bool> match, int nth)
    {
        foreach (var f in owner.GetFields(Declared))
        {
            if (f.IsStatic || f.IsLiteral || !match(f)) continue;
            if (nth-- == 0) return f;
        }
        return null;
    }

    static int OffsetOf(Il2CppType_ owner, Il2CppFieldInfo field)
    {
        var klass = IL2CPP.il2cpp_class_from_system_type(owner.Pointer);
        var native = IL2CPP.GetIl2CppField(klass, field.Name);
        return (int)IL2CPP.il2cpp_field_get_offset(native);
    }
}
