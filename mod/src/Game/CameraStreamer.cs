using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Dolophobia.Game;

/// <summary>
/// Renders watched CCTV cameras into a small offscreen texture and JPEG-encodes them.
/// Runs on the main thread and stalls on readback, so keep resolution and fps modest.
/// Frames go out as binary: [0x01][camera index][jpeg bytes].
/// </summary>
internal sealed class CameraStreamer : IDisposable
{
    public const byte FrameTag = 0x01;

    RenderTexture _rt;
    Texture2D _tex;
    float _next;

    public void Tick(World world, float now, IEnumerable<int> watched, Action<byte[]> emit)
    {
        var cfg = Plugin.Settings;
        if (now < _next) return;
        _next = now + 1f / Mathf.Max(0.5f, cfg.CameraFps.Value);

        var wanted = watched.Distinct().Where(i => i >= 0 && i < world.Cameras.Count).ToList();
        if (wanted.Count == 0) return;
        EnsureTargets(cfg.CameraWidth.Value, cfg.CameraHeight.Value);

        foreach (var index in wanted)
        {
            var cctv = world.Cameras[index];
            var cam = cctv == null ? null : Shapes.CCTV_Camera.Value?.ReadRef<Camera>(cctv) ?? cctv.GetComponentInChildren<Camera>(true);
            if (cam == null) continue;

            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                cam.targetTexture = _rt;
                cam.Render();
                RenderTexture.active = _rt;
                _tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
            }

            var jpg = ImageConversion.EncodeToJPG(_tex, cfg.CameraJpegQuality.Value);
            var frame = new byte[jpg.Length + 2];
            frame[0] = FrameTag;
            frame[1] = (byte)index;
            for (var i = 0; i < jpg.Length; i++) frame[i + 2] = jpg[i];
            emit(frame);
        }
    }

    void EnsureTargets(int width, int height)
    {
        if (_rt != null && _rt.width == width && _rt.height == height) return;
        Dispose();
        _rt = new RenderTexture(width, height, 24);
        _tex = new Texture2D(width, height, TextureFormat.RGB24, false);
    }

    public void Dispose()
    {
        if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); _rt = null; }
        if (_tex != null) { UnityEngine.Object.Destroy(_tex); _tex = null; }
    }
}
