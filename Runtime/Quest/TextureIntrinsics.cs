using Meta.XR;
using UnityEngine;

// Maps PassthroughCameraAccess's sensor intrinsics onto the texture it actually
// delivers, in the pixel space the AprilTag detector sees after
// PassthroughCameraSource flips the texture so row 0 is the top of the image.
public static class TextureIntrinsics
{
    // Intrinsics.FocalLength/PrincipalPoint are reported relative to
    // Intrinsics.SensorResolution, which can differ from the delivered texture
    // (confirmed: SensorResolution reports 1280x1280 while the live texture is
    // 1280x960). The texture is NOT a non-uniform resize of the sensor: it is the
    // sensor image center-cropped to the texture's aspect ratio, then scaled
    // uniformly - mirroring PassthroughCameraAccess.CalcSensorCropRegion() in
    // MRUK. For 1280x1280 -> 1280x960 that is a 160px crop top and bottom with a
    // scale of 1, which is why the raw (unscaled) focal length tested correct.
    //
    // Sensor coordinates have their origin at the bottom-left (MRUK's viewport
    // convention), while the detector's image has row 0 at the top, so the
    // principal point's Y is flipped as well.
    public static PinholeIntrinsics ForTexture(PassthroughCameraAccess.CameraIntrinsics intrinsics, int width, int height)
    {
        var sensorResolution = (Vector2)intrinsics.SensorResolution;
        var scaleFactor = new Vector2(width, height) / sensorResolution;
        scaleFactor /= Mathf.Max(scaleFactor.x, scaleFactor.y);

        var cropOrigin = sensorResolution * (Vector2.one - scaleFactor) * 0.5f;
        var cropWidth = sensorResolution.x * scaleFactor.x;
        var pixelsPerSensorPixel = width / cropWidth;

        var fx = intrinsics.FocalLength.x * pixelsPerSensorPixel;
        var fy = intrinsics.FocalLength.y * pixelsPerSensorPixel;
        var cx = (intrinsics.PrincipalPoint.x - cropOrigin.x) * pixelsPerSensorPixel;
        var cyFromBottom = (intrinsics.PrincipalPoint.y - cropOrigin.y) * pixelsPerSensorPixel;

        return new PinholeIntrinsics(width, height, fx, fy, cx, height - cyFromBottom);
    }
}
