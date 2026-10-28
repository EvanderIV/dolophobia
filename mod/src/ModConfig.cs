using System;
using System.Security.Cryptography;
using BepInEx.Configuration;

namespace Dolophobia;

/// <summary>Lives in BepInEx/config/dev.dolophobia.mod.cfg.</summary>
internal sealed class ModConfig
{
    public readonly ConfigEntry<string> RelayUrl;
    public readonly ConfigEntry<string> RelaySecret;
    public readonly ConfigEntry<string> HostKey;

    public readonly ConfigEntry<string> GhostPin;
    public readonly ConfigEntry<string> InternPin;
    public readonly ConfigEntry<string> AdminPin;
    public readonly ConfigEntry<int> MaxInterns;

    public readonly ConfigEntry<float> GhostEventCooldown;
    public readonly ConfigEntry<float> GhostHuntCooldown;
    public readonly ConfigEntry<bool> GhostCanHunt;

    public readonly ConfigEntry<float> ViewHz;
    public readonly ConfigEntry<float> CameraFps;
    public readonly ConfigEntry<int> CameraWidth;
    public readonly ConfigEntry<int> CameraHeight;
    public readonly ConfigEntry<int> CameraJpegQuality;

    public ModConfig(ConfigFile cfg)
    {
        RelayUrl = cfg.Bind("Relay", "Url", "ws://localhost:4787/host",
            "WebSocket URL of the Dolophobia relay's /host endpoint.");
        RelaySecret = cfg.Bind("Relay", "Secret", "",
            "Must match RELAY_HOST_SECRET on the relay, if the relay sets one.");
        HostKey = cfg.Bind("Relay", "HostKey", RandomToken(24),
            "Identifies this host to the relay so the lobby code survives reconnects. Keep private.");

        GhostPin = cfg.Bind("Auth", "GhostPin", "",
            "PIN web players enter to join as the Ghost. Blank = new random PIN each launch.");
        InternPin = cfg.Bind("Auth", "InternPin", "",
            "PIN web players enter to join as an Intern. Blank = new random PIN each launch.");
        AdminPin = cfg.Bind("Auth", "AdminPin", "",
            "PIN for the host's own admin console. Blank = new random PIN each launch.");
        MaxInterns = cfg.Bind("Auth", "MaxInterns", 3, "How many intern sessions may be connected at once.");

        GhostEventCooldown = cfg.Bind("Ghost", "EventCooldownSeconds", 8f,
            "Minimum seconds between ghost-triggered events (doors, throws, knocks...).");
        GhostHuntCooldown = cfg.Bind("Ghost", "HuntCooldownSeconds", 120f,
            "Minimum seconds between ghost-triggered hunts.");
        GhostCanHunt = cfg.Bind("Ghost", "CanForceHunt", true, "Whether the web ghost may start hunts.");

        ViewHz = cfg.Bind("Streaming", "ViewHz", 10f, "How often state snapshots are pushed to the relay.");
        CameraFps = cfg.Bind("Streaming", "CameraFps", 4f, "Frames per second per watched CCTV camera.");
        CameraWidth = cfg.Bind("Streaming", "CameraWidth", 320, "CCTV stream width in pixels.");
        CameraHeight = cfg.Bind("Streaming", "CameraHeight", 240, "CCTV stream height in pixels.");
        CameraJpegQuality = cfg.Bind("Streaming", "CameraJpegQuality", 45, "CCTV JPEG quality (1-100).");
    }

    public static string RandomToken(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public static string RandomPin()
    {
        // No 0/O/1/I so PINs survive being read aloud over voice chat.
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<char> pin = stackalloc char[6];
        for (var i = 0; i < pin.Length; i++) pin[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(pin);
    }
}
