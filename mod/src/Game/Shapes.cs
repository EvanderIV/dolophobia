using System;
using System.Linq;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Dolophobia.Game;

/// <summary>
/// Every obfuscated field the mod depends on, resolved by shape and named Owner_Field.
/// Offsets in comments are from the Cpp2IL dump of the build this was written against
/// (Unity 2022.3.40, metadata v31) and only help re-verify after a game update; the code never uses them.
/// </summary>
internal static class Shapes
{
    // GhostAI: `public GhostAI.<state enum> state @0x30`. Values: idle, wander, hunting, favouriteRoom, ...
    public static readonly Lazy<ObfField> GhostAI_State = new(() =>
        Obf.Find<GhostAI>("GhostAI.state", f => Obf.IsEnumWith(f.FieldType, "hunting")));

    public static readonly Lazy<string[]> GhostStateNames = new(() => Obf.EnumNamesWith<GhostAI>("hunting"));

    // GhostAI: `public GhostInfo ghostInfo @0x38`
    public static readonly Lazy<ObfField> GhostAI_Info = new(() =>
        Obf.Find<GhostAI>("GhostAI.ghostInfo", f => Obf.Is<GhostInfo>(f.FieldType)));

    // GhostAI: `public GhostInteraction ghostInteraction @0x50`
    public static readonly Lazy<ObfField> GhostAI_Interaction = new(() =>
        Obf.Find<GhostAI>("GhostAI.ghostInteraction", f => Obf.Is<GhostInteraction>(f.FieldType)));

    // GhostInfo: `public LevelRoom favouriteRoom @0x70`
    public static readonly Lazy<ObfField> GhostInfo_Room = new(() =>
        Obf.Find<GhostInfo>("GhostInfo.favouriteRoom", f => Obf.Is<LevelRoom>(f.FieldType)));

    // GhostInfo: `public <GhostTraits struct> traits @0x28`, whose first member is the ghost type enum
    // (Spirit, Wraith, Phantom, ... Deildegast). A second member of the same enum type follows (mimic/secondary).
    public static readonly Lazy<ObfField> GhostInfo_Type = new(() =>
        Obf.FindNested<GhostInfo>("GhostInfo.traits.ghostType", HasGhostTypeEnum, IsGhostTypeEnum));

    public static readonly Lazy<string[]> GhostTypeNames = new(() =>
    {
        var traits = Obf.FindInfo(Il2CppType.Of<GhostInfo>(), f => f.FieldType.IsValueType && HasGhostTypeEnum(f), 0);
        var type = traits == null ? null : Obf.FindInfo(traits.FieldType, IsGhostTypeEnum, 0);
        return type?.FieldType.GetEnumNames().ToArray() ?? Array.Empty<string>();
    });

    // Player: `public PlayerSanity playerSanity @0x90`
    public static readonly Lazy<ObfField> Player_Sanity = new(() =>
        Obf.Find<Player>("Player.playerSanity", f => Obf.Is<PlayerSanity>(f.FieldType)));

    // Player: `public LevelRoom currentRoom @0x50`
    public static readonly Lazy<ObfField> Player_Room = new(() =>
        Obf.Find<Player>("Player.currentRoom", f => Obf.Is<LevelRoom>(f.FieldType)));

    // PlayerSanity: `public float insanity @0x30` -- the only public float. 0 = fully sane, 100 = fully insane.
    // VERIFY IN GAME: if the dashboard shows sanity going *up* as players get scared, flip SanityFromInsanity.
    public static readonly Lazy<ObfField> PlayerSanity_Insanity = new(() =>
        Obf.Find<PlayerSanity>("PlayerSanity.insanity", f => f.IsPublic && f.FieldType.FullName == "System.Single"));

    // CCTV: `public Camera camera @0x3E0`
    public static readonly Lazy<ObfField> CCTV_Camera = new(() =>
        Obf.Find<CCTV>("CCTV.camera", f => Obf.Is<Camera>(f.FieldType)));

    public static float SanityFromInsanity(float insanity) => Mathf.Clamp(100f - insanity, 0f, 100f);

    public static string NameAt(string[] names, int index) =>
        index >= 0 && index < names.Length ? names[index] : index.ToString();

    static bool IsGhostTypeEnum(Il2CppSystem.Reflection.FieldInfo f) => Obf.IsEnumWith(f.FieldType, "Banshee");

    static bool HasGhostTypeEnum(Il2CppSystem.Reflection.FieldInfo f) => Obf.FindInfo(f.FieldType, IsGhostTypeEnum, 0) != null;
}
