using System;
using HarmonyLib;
using UnityEngine;
using Photon.Pun;

namespace Dolophobia.Game;

/// <summary>
/// Harmony hooks. Only readable (non-obfuscated) method names are patched: Unity messages,
/// public API, and [PunRPC] methods, whose names Photon needs intact to dispatch them.
/// Parameters are taken positionally (__0, __1) because their names are obfuscated.
/// Every hook is wrapped so a game update can never turn into an exception inside game code.
/// </summary>
[HarmonyPatch]
internal static class GamePatches
{
    // Truck motion-sensor log. Fires on every client when any placed sensor trips.
    [HarmonyPostfix, HarmonyPatch(typeof(MotionSensorData), nameof(MotionSensorData.Detected))]
    static void MotionDetected(MotionSensor __0) => Safe(() =>
        GameEvents.Raise("motion", Audience.Truck, __0 == null ? (Vector3?)null : __0.transform.position));

    // Truck sound-sensor bars: (sensor index, level).
    [HarmonyPostfix, HarmonyPatch(typeof(SoundSensorData), nameof(SoundSensorData.UpdateSensorValue))]
    static void SoundSensorValue(int __0, float __1) => Safe(() => GameEvents.SoundSensorLevels[__0] = __1);

    // Ghost footsteps (also what the parabolic mic / sound sensor picks up).
    [HarmonyPostfix, HarmonyPatch(typeof(GhostInteraction), "SpawnFootstepNetworked")]
    static void Footstep(Vector3 __0) => Safe(() => GameEvents.Raise("paranormal_sound", Audience.Truck, __0,
        new() { ["sound"] = "footstep" }));

    [HarmonyPostfix, HarmonyPatch(typeof(GhostInteraction), "PlayThrowingNoise")]
    static void Throw(Vector3 __0) => Safe(() => GameEvents.Raise("paranormal_sound", Audience.Truck, __0,
        new() { ["sound"] = "throw" }));

    [HarmonyPostfix, HarmonyPatch(typeof(GhostInteraction), "SpawnGhostWhisperNetworked")]
    static void Whisper(Vector3 __0) => Safe(() => GameEvents.Raise("paranormal_sound", Audience.Truck, __0,
        new() { ["sound"] = "whisper" }));

    [HarmonyPostfix, HarmonyPatch(typeof(GhostInteraction), "SpawnEMFNetworked")]
    static void Emf(Vector3 __0) => Safe(() => GameEvents.Raise("emf", Audience.Truck, __0));

    // Death RPC, runs on every client. The coroutine itself is untouched; we only note who died.
    [HarmonyPostfix, HarmonyPatch(typeof(Player), "StartDeathAnimation")]
    static void Died(Player __instance) => Safe(() =>
    {
        var actor = __instance.GetComponent<PhotonView>()?.Owner?.ActorNumber;
        if (actor == null) return;
        World.DeadActors.Add(actor.Value);
        GameEvents.Raise("player_died", Audience.Everyone, __instance.transform.position,
            new() { ["actor"] = actor.Value });
    });

    [HarmonyPostfix, HarmonyPatch(typeof(Player), nameof(Player.RevivePlayer))]
    static void Revived(Player __instance) => Safe(() =>
    {
        var actor = __instance.GetComponent<PhotonView>()?.Owner?.ActorNumber;
        if (actor != null) World.DeadActors.Remove(actor.Value);
    });

    static void Safe(Action hook)
    {
        try { hook(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Patches] {e}"); }
    }
}
