using System;
using System.Diagnostics;
using AprilTag.Interop;
using Meta.XR;
using UnityEngine;
using Debug = UnityEngine.Debug;
using UnityEngine.Rendering;

// Meta Quest camera source: reads PassthroughCameraAccess's texture back from
// the GPU and maps its sensor intrinsics onto that texture. If no camera is
// assigned, the first PassthroughCameraAccess in the scene is used.
public class PassthroughCameraSource : AprilTagCameraSource
{
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    private ImageU8 image;
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
            pixelBuffer = new Color32[tex.width * tex.height];
            Debug.Log($"[PassthroughCameraSource] Texture {tex.width}x{tex.height}, sensor {cameraAccess.Intrinsics.SensorResolution}");
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

    // Unity textures are bottom-up; the detector wants row 0 at the top, and a
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

    private void OnDestroy()
    {
        image?.Dispose();
        image = null;
    }
}
