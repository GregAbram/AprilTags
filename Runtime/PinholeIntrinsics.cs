// Pinhole intrinsics in the pixel space of the image the AprilTag detector
// sees, with row 0 at the top of the image.
public readonly struct PinholeIntrinsics
{
    public readonly int Width;
    public readonly int Height;
    public readonly float Fx;
    public readonly float Fy;
    public readonly float Cx;
    public readonly float Cy;

    public PinholeIntrinsics(int width, int height, float fx, float fy, float cx, float cy)
    {
        Width = width;
        Height = height;
        Fx = fx;
        Fy = fy;
        Cx = cx;
        Cy = cy;
    }

    public override string ToString() => $"{Width}x{Height} fx={Fx:F1} fy={Fy:F1} cx={Cx:F1} cy={Cy:F1}";
}
