using System;
using System.IO;
using UnityEngine;

// Quest: one room for every app on the headset. A survey saved there is also
// written to SharedRoom.Folder, and at startup every app on this package takes
// that room when it's newer than its own saved one (AprilTagRoomLocalizer), so
// a room surveyed once needs no room code scanned in each app. A room code
// scanned later is newer, and wins until the next survey is saved.
//
// Reading and writing /sdcard needs All files access: the package's Quest
// build declares MANAGE_EXTERNAL_STORAGE, and each app is granted it once with
//   adb shell appops set <app id> MANAGE_EXTERNAL_STORAGE allow
// Android only; iOS apps can't share files this way, so iPhones scan the code.
public static class SharedRoom
{
    public const string Folder = "/sdcard/AprilTags";
    public static string ConfigPath => Folder + "/room_config.json";

    public static bool Supported => Application.platform == RuntimePlatform.Android;

    // The virtual room's reference tags, which a new survey starts from
    // (RoomSurvey.NewSurvey): a room config whose measured tags place the
    // virtual room's frame, e.g. one tag hand-measured from the floor. On Quest
    // in Folder, editable with adb; elsewhere in the app's persistentDataPath.
    public static string BasePath => Supported
        ? Folder + "/room_base.json"
        : Path.Combine(Application.persistentDataPath, "room_base.json");

    // The base config, if there is one with at least one tag.
    public static bool TryReadBase(out RoomConfig config)
    {
        config = null;
        try
        {
            if (!File.Exists(BasePath))
            {
                return false;
            }
            config = JsonUtility.FromJson<RoomConfig>(File.ReadAllText(BasePath));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SharedRoom] Can't read {BasePath} ({e.Message}); a new survey starts from the first tag seen");
            return false;
        }
        if (config?.tags == null || config.tags.Length == 0)
        {
            Debug.LogWarning($"[SharedRoom] {BasePath} lists no tags; a new survey starts from the first tag seen");
            config = null;
            return false;
        }
        return true;
    }

    public static string GrantCommand =>
        $"adb shell appops set {Application.identifier} MANAGE_EXTERNAL_STORAGE allow";

    // Android's All files access; null where it can't be told.
    public static bool? HasAllFilesAccess()
    {
        if (!Supported)
        {
            return null;
        }
        try
        {
            using var environment = new AndroidJavaClass("android.os.Environment");
            return environment.CallStatic<bool>("isExternalStorageManager");
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Writes the room, its room code text and (if given) its QR image to Folder.
    // Returns false with a reason when it can't.
    public static bool TryWrite(RoomConfig config, Texture2D codeImage, out string error)
    {
        error = null;
        if (!Supported)
        {
            error = "only on Quest";
            return false;
        }
        if (HasAllFilesAccess() == false)
        {
            error = $"no All files access ({GrantCommand})";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(config, true));
            if (File.Exists(ConfigPath))
            {
                File.Delete(ConfigPath);
            }
            File.Move(temp, ConfigPath);
            File.WriteAllText(Path.Combine(Folder, "room_code.txt"), RoomCode.Encode(config));
            if (codeImage != null)
            {
                File.WriteAllBytes(Path.Combine(Folder, "room_code.png"), codeImage.EncodeToPNG());
            }
            Debug.Log($"[SharedRoom] Survey {RoomCode.SurveyId(config)} shared with the other apps on this headset: {ConfigPath}");
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    // The shared room and when it was written (UTC). False if there's none, or
    // it can't be read (logged once, with how to grant access).
    public static bool TryRead(out RoomConfig config, out DateTime writtenUtc)
    {
        config = null;
        writtenUtc = default;
        if (!Supported || !File.Exists(ConfigPath))
        {
            if (Supported && Directory.Exists(Folder) && HasAllFilesAccess() == false)
            {
                Debug.LogWarning($"[SharedRoom] {Folder} is there but this app can't read it, so it doesn't share the " +
                                 $"headset's surveyed room. Grant All files access: {GrantCommand}");
            }
            return false;
        }
        try
        {
            config = JsonUtility.FromJson<RoomConfig>(File.ReadAllText(ConfigPath));
            writtenUtc = File.GetLastWriteTimeUtc(ConfigPath);
            if (config?.tags == null || config.tags.Length == 0)
            {
                Debug.LogWarning($"[SharedRoom] {ConfigPath} lists no tags; ignoring it");
                config = null;
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SharedRoom] Can't read {ConfigPath} ({e.Message}). Grant All files access: {GrantCommand}");
            config = null;
            return false;
        }
    }
}
