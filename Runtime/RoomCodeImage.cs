using System.IO;
using UnityEngine;

// Draws room codes as QR images, through an encoder the optional
// TaccAprilTags.RoomCode assembly (ZXing) installs. Without it, the code's
// text is still saved, and any QR generator can make the image.
public interface IRoomCodeEncoder
{
    Texture2D Render(string text, int pixels);
}

public static class RoomCodeImage
{
    public static IRoomCodeEncoder Encoder { get; set; }

    // Writes room_code.txt (the code's text) and, with an encoder, room_code.png
    // to persistentDataPath; returns the texture (null without an encoder).
    public static Texture2D Save(RoomConfig config, int pixels = 1024)
    {
        var text = RoomCode.Encode(config);
        var folder = Application.persistentDataPath;
        File.WriteAllText(Path.Combine(folder, "room_code.txt"), text);
        var texture = Encoder?.Render(text, pixels);
        if (texture != null)
        {
            File.WriteAllBytes(Path.Combine(folder, "room_code.png"), texture.EncodeToPNG());
        }
        Debug.Log($"[RoomCodeImage] Room code (survey {RoomCode.SurveyId(config)}, {text.Length} characters){(texture != null ? " and room_code.png" : "")} saved to {folder}: {text}");
        return texture;
    }
}
