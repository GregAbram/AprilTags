using System;
using AprilTag.Interop;
using UnityEngine;

// One grayscale camera frame ready for detection, with the camera's world pose
// at the moment the frame was captured (not when it finished processing).
public readonly struct CameraFrame
{
    // Row 0 is the top of the image. Owned by the source; valid only during the
    // callback that delivers it.
    public readonly ImageU8 Image;
    public readonly Vector3 CameraPosition;
    public readonly Quaternion CameraRotation;

    public CameraFrame(ImageU8 image, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        Image = image;
        CameraPosition = cameraPosition;
        CameraRotation = cameraRotation;
    }
}

// Where AprilTagRoomLocalizer gets its images: one implementation per platform
// (PassthroughCameraSource for Meta Quest).
public abstract class AprilTagCameraSource : MonoBehaviour
{
    // False while the camera isn't delivering frames (not started yet, or paused).
    public abstract bool IsPlaying { get; }

    // Intrinsics in the pixel space of the frames this source delivers. False
    // until the frame size and intrinsics are known.
    public abstract bool TryGetIntrinsics(out PinholeIntrinsics intrinsics);

    // Starts capturing a frame. Returns false if none can be requested right now.
    // Otherwise onComplete is called exactly once - possibly before this returns -
    // with true and the frame, or with false if the capture failed.
    public abstract bool TryRequestFrame(Action<bool, CameraFrame> onComplete);
}
