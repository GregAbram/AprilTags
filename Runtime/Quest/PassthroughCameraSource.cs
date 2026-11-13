using System;
using System.Diagnostics;
using AprilTag.Interop;
using Meta.XR;
using UnityEngine;
using Debug = UnityEngine.Debug;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

// Meta Quest camera source: reads PassthroughCameraAccess's texture back from
// the GPU and maps its sensor intrinsics onto that texture. If no camera is
// assigned, the first PassthroughCameraAccess in the scene is used.
//
// The GPU first copies the frame, flipped to top-down, into a one-channel
// (R8) texture of the same size, so the readback is a quarter of the camera's
// RGBA frame and needs no conversion on the CPU: a full-size RGBA readback
// every frame made the headset drop frames while scanning. The red channel
// serves as grayscale; black-on-white tags read the same in any channel.
public class PassthroughCameraSource : AprilTagCameraSource
{
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    private ImageU8 image;
    private RenderTexture gray;   // R8, top-down; null where R8 can't be rendered (RGBA fallback)
    private Color32[] pixelBuffer;

    // For components that add the source at runtime; call before its Start().
    public void Configure(PassthroughCameraAccess camera)
    {
        cameraAccess = camera;
    }

    // The camera pauses with the app (e.g. headset taken off); querying it then
    // logs an error every frame, so everything below checks this first.
    public override bool IsPlaying => cameraAccess != null && cameraAccess.IsPlaying;

    private void Start()
    {
        if (cameraAccess == null)
        {
            cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
        }
        if (cameraAccess == null)
        {
            Debug.LogError("[PassthroughCameraSource] No PassthroughCameraAccess assigned or found in the scene - no frames will be delivered");
        }
    }

    public override bool TryGetIntrinsics(out PinholeIntrinsics intrinsics)
    {
        intrinsics = default;
        if (!IsPlaying)
        {
            return false;
        }

        var tex = cameraAccess.GetTexture();
        if (tex == null)
        {
            return false;
        }

        if (image == null || image.Width != tex.width || image.Height != tex.height)
        {
            image?.Dispose();
            image = ImageU8.Create(tex.width, tex.height);
            ReleaseGray();
            // sRGB where it can be rendered, so the values match the camera
            // texture's bytes as the RGBA readback had them.
            var format = SystemInfo.IsFormatSupported(GraphicsFormat.R8_SRGB, GraphicsFormatUsage.Render)
                ? GraphicsFormat.R8_SRGB
                : SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.Render)
                    ? GraphicsFormat.R8_UNorm
                    : GraphicsFormat.None;
            if (format != GraphicsFormat.None)
            {
                gray = new RenderTexture(tex.width, tex.height, format, GraphicsFormat.None) { name = "AprilTag gray" };
                gray.Create();
                pixelBuffer = null;
            }
            else
            {
                pixelBuffer = new Color32[tex.width * tex.height];
            }
            Debug.Log($"[PassthroughCameraSource] Texture {tex.width}x{tex.height}, sensor {cameraAccess.Intrinsics.SensorResolution}, " +
                      (gray != null ? $"read back as one channel ({gray.graphicsFormat})" : "read back as RGBA"));
        }

        intrinsics = TextureIntrinsics.ForTexture(cameraAccess.Intrinsics, tex.width, tex.height);
        return true;
    }

    public override bool TryRequestFrame(Action<bool, CameraFrame> onComplete)
    {
        if (!IsPlaying || image == null)
        {
            return false;
        }

        var tex = cameraAccess.GetTexture();
        if (tex == null)
        {
            return false;
        }

        // Capture the camera pose now, alongside the texture - not later when the
        // async readback completes, since that's 1+ frames later and would be
        // mismatched with this image if the head is moving.
        var cameraPose = cameraAccess.GetCameraPose();

        if (gray != null)
        {
            // Flipped on the way: Unity textures are bottom-up, the detector wants row 0 at the top.
            Graphics.Blit(tex, gray, new Vector2(1f, -1f), new Vector2(0f, 1f));
            AsyncGPUReadback.Request(gray, 0, request =>
            {
                if (request.hasError || image == null)
                {
                    onComplete(false, default);
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                CopyRows(request.GetData<byte>(), image);
                LastFramePrepareMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                onComplete(true, new CameraFrame(image, cameraPose.position, cameraPose.rotation));
            });
            return true;
        }

        AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBA32, request =>
        {
            if (request.hasError || image == null)
            {
                onComplete(false, default);
                return;
            }

            var start = Stopwatch.GetTimestamp();
            request.GetData<Color32>().CopyTo(pixelBuffer);
            ConvertToImageU8(pixelBuffer, image);
            LastFramePrepareMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            onComplete(true, new CameraFrame(image, cameraPose.position, cameraPose.rotation));
        });
        return true;
    }

    // Already top-down and one byte a pixel: row by row, since the detector's
    // rows may be padded (stride).
    private static void CopyRows(Unity.Collections.NativeArray<byte> src, ImageU8 image)
    {
        var width = image.Width;
        var stride = image.Stride;
        var dst = image.Buffer;
        if (stride == width)
        {
            src.AsReadOnlySpan().Slice(0, width * image.Height).CopyTo(dst);
            return;
        }
        var row = src.AsReadOnlySpan();
        for (var y = 0; y < image.Height; y++)
        {
            row.Slice(y * width, width).CopyTo(dst.Slice(y * stride, width));
        }
    }

    // RGBA fallback. Unity textures are bottom-up; the detector wants row 0 at the top, and a
    // mirrored image never decodes. The green channel serves as grayscale.
    private static void ConvertToImageU8(Color32[] src, ImageU8 image)
    {
        var width = image.Width;
        var height = image.Height;
        var stride = image.Stride;
        var dst = image.Buffer;

        var offsSrc = 0;
        var offsDst = stride * (height - 1);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                dst[offsDst + x] = src[offsSrc + x].g;
            }
            offsSrc += width;
            offsDst -= stride;
        }
    }

    private void ReleaseGray()
    {
        if (gray != null)
        {
            gray.Release();
            Destroy(gray);
            gray = null;
        }
    }

    private void OnDestroy()
    {
        image?.Dispose();
        image = null;
        ReleaseGray();
    }
}
