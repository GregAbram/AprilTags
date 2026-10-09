using AprilTag.Interop;

// Reads a QR code from a grayscale camera frame (row 0 at the top). Called on
// the localizer's worker thread; returns the code's text, or null if none.
// The optional TaccAprilTags.RoomCode assembly provides one (ZXing) and
// installs it as AprilTagRoomLocalizer.DefaultRoomCodeDecoder.
public interface IRoomCodeDecoder
{
    string Decode(ImageU8 image);
}
