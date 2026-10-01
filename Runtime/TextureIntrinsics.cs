using Meta.XR;
using UnityEngine;

// Pinhole intrinsics expressed in the pixel space the AprilTag detector actually
// sees: the texture delivered by PassthroughCameraAccess.GetTexture(), after
// ConvertToImageU8 flips it so row 0 is the top of the image.
public readonly struct TextureIntrinsics
{
    public readonly float Fx;
    public readonly float Fy;
    public readonly float Cx;
    public readonly float Cy;

    private TextureIntrinsics(float fx, float fy, float cx, float cy)
    {
        Fx = fx;
        Fy = fy;
        Cx = cx;
        Cy = cy;
    }

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
    public static TextureIntrinsics ForTexture(PassthroughCameraAccess.CameraIntrinsics intrinsics, int width, int height)
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

        return new TextureIntrinsics(fx, fy, cx, height - cyFromBottom);
    }

    public override string ToString() => $"fx={Fx:F1} fy={Fy:F1} cx={Cx:F1} cy={Cy:F1}";
}
