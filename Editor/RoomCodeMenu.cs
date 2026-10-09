using System.IO;
using UnityEditor;
using UnityEngine;

// Tools > AprilTags > Save Room Code Image: turns the project's
// StreamingAssets/room_config.json into a printable QR code (room_code.png).
// Batchmode: -executeMethod RoomCodeMenu.SaveFromStreamingAssets (writes
// room_code.png and room_code.txt next to the project's Assets folder).
public static class RoomCodeMenu
{
    private const string ConfigPath = "Assets/StreamingAssets/room_config.json";

    [MenuItem("Tools/AprilTags/Save Room Code Image...")]
    public static void SaveWithDialog()
    {
        var path = EditorUtility.SaveFilePanel("Save room code", "", "room_code.png", "png");
        if (!string.IsNullOrEmpty(path))
        {
            Save(path);
        }
    }

    public static void SaveFromStreamingAssets() => Save("room_code.png");

    private static void Save(string pngPath)
    {
        ZXingRoomCode.Install();
        var config = JsonUtility.FromJson<RoomConfig>(File.ReadAllText(ConfigPath));
        var text = RoomCode.Encode(config);
        var texture = RoomCodeImage.Encoder.Render(text, 1024);
        File.WriteAllBytes(pngPath, texture.EncodeToPNG());
        File.WriteAllText(Path.ChangeExtension(pngPath, ".txt"), text);
        Debug.Log($"[RoomCodeMenu] Room code (survey {RoomCode.SurveyId(config)}, {text.Length} characters) saved to {pngPath}: {text}");
    }
}
