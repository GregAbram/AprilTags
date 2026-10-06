using System;
using AprilTag.Interop;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

// AR Foundation camera source (ARKit on iPhone): copies the luminance (Y) plane
// of the latest CPU camera image - already grayscale, so no GPU readback - and
// reports the camera intrinsics for that image. If no camera manager is
// assigned, the first ARCameraManager in the scene is used.
//
// The CPU image is always in the sensor's native landscape orientation (the
// screen's LandscapeLeft: home button / USB port on the right), whatever way
// the device is held, while the AR camera's transform follows the screen. The
// camera pose is rolled about its view axis to match the image; see
// RollDegreesFor.
public class ARFoundationCameraSource : AprilTagCameraSource
{
    [SerializeField] private ARCameraManager cameraManager;

    // ARKit's CPU image should already have row 0 at the top. If tags are never
    // detected on a new device, try toggling this first (a mirrored code never decodes).
    [SerializeField] private bool flipVertical = false;

    private ImageU8 image;
    private double lastFrameTimestamp = double.NaN;

    // For components that add the source at runtime; call before its Start().
    public void Configure(ARCameraManager manager)
    {
        cameraManager = manager;
    }

    public override bool IsPlaying =>
        cameraManager != null && cameraManager.isActiveAndEnabled && ARSession.state == ARSessionState.SessionTracking;

    private void Start()
    {
        if (cameraManager == null)
        {
            cameraManager = FindAnyObjectByType<ARCameraManager>();
        }
        if (cameraManager == null)
        {
            Debug.LogError("[ARFoundationCameraSource] No ARCameraManager assigned or found in the scene - no frames will be delivered");
        }
    }

    public override bool TryGetIntrinsics(out PinholeIntrinsics intrinsics)
    {
        intrinsics = default;
        if (!IsPlaying || !cameraManager.TryGetIntrinsics(out var cameraIntrinsics))
        {
            return false;
        }

        int width, height;
        using (var cpuImage = AcquireLatestCpuImage())
        {
            if (!cpuImage.valid)
            {
                return false;
            }
            width = cpuImage.width;
            height = cpuImage.height;
        }

        if (image == null || image.Width != width || image.Height != height)
        {
            image?.Dispose();
            image = ImageU8.Create(width, height);
            Debug.Log($"[ARFoundationCameraSource] CPU image {width}x{height}, intrinsics reported for {cameraIntrinsics.resolution}, screen {Screen.orientation}, flipVertical={flipVertical}");
        }

        // Intrinsics are reported for the camera's full resolution; the CPU image
        // normally has exactly that size, but scale uniformly if it doesn't.
        var scale = (float)width / cameraIntrinsics.resolution.x;
        var fx = cameraIntrinsics.focalLength.x * scale;
        var fy = cameraIntrinsics.focalLength.y * scale;
        var cx = cameraIntrinsics.principalPoint.x * scale;
        var cy = cameraIntrinsics.principalPoint.y * scale;
        if (flipVertical)
        {
            cy = height - cy;
        }

        intrinsics = new PinholeIntrinsics(width, height, fx, fy, cx, cy);
        return true;
    }

    public override bool TryRequestFrame(Action<bool, CameraFrame> onComplete)
    {
        if (!IsPlaying || image == null)
        {
            return false;
        }

        using var cpuImage = AcquireLatestCpuImage();

        // ARKit runs at 60 Hz, possibly slower than the app: don't detect the
        // same image twice.
        if (!cpuImage.valid || cpuImage.timestamp == lastFrameTimestamp)
        {
            return false;
        }
        if (cpuImage.width != image.Width || cpuImage.height != image.Height)
        {
            Debug.LogWarning($"[ARFoundationCameraSource] CPU image size changed to {cpuImage.width}x{cpuImage.height}; frame skipped");
            return false;
        }
        lastFrameTimestamp = cpuImage.timestamp;

        // The pose is read alongside the image, before anything else can update it.
        var cameraTransform = cameraManager.transform;
        var cameraRotation = cameraTransform.rotation * Quaternion.Euler(0f, 0f, RollDegreesFor(Screen.orientation));

        CopyLuminance(cpuImage.GetPlane(0), image, flipVertical);
        onComplete(true, new CameraFrame(image, cameraTransform.position, cameraRotation));
        return true;
    }

    private XRCpuImage AcquireLatestCpuImage()
    {
        return cameraManager.TryAcquireLatestCpuImage(out var cpuImage) ? cpuImage : default;
    }

    // Rotation about the camera's view axis taking image axes (x right, y up, as
    // in the native landscape image) to the AR camera's axes for the current
    // screen orientation. E.g. in Portrait the image's right points down the
    // screen and its up points right: a -90 degree roll.
    private static float RollDegreesFor(ScreenOrientation orientation)
    {
        return orientation switch
        {
            ScreenOrientation.Portrait => -90f,
            ScreenOrientation.PortraitUpsideDown => 90f,
            ScreenOrientation.LandscapeRight => 180f,
            _ => 0f,
        };
    }

    private static void CopyLuminance(XRCpuImage.Plane plane, ImageU8 image, bool flipVertical)
    {
        var width = image.Width;
        var height = image.Height;
        var stride = image.Stride;
        var dst = image.Buffer;
        var src = plane.data.AsReadOnlySpan();

        for (var y = 0; y < height; y++)
        {
            var dstRow = dst.Slice((flipVertical ? height - 1 - y : y) * stride, width);
            var srcOffset = y * plane.rowStride;
            if (plane.pixelStride == 1)
            {
                src.Slice(srcOffset, width).CopyTo(dstRow);
            }
            else
            {
                for (var x = 0; x < width; x++)
                {
                    dstRow[x] = src[srcOffset + x * plane.pixelStride];
                }
            }
        }
    }

    private void OnDestroy()
    {
        image?.Dispose();
        image = null;
    }
}
