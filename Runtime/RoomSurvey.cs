using System.Collections;
using UnityEngine;

// Runs a survey app's session around a RoomAnchor in survey mode: the room's
// name, the printed tag size and an optional DataManager address (kept between
// runs), starting a new survey, and saving the result with its room code.
//
// With no room config on the device, a new survey starts at once: no tags
// listed, every tag learned at TagSizeMeters, and the first one seen defines
// the room. A survey saved earlier (room_config.json in persistentDataPath)
// loads instead, so the next run extends it; NewSurvey() starts from nothing.
public class RoomSurvey : MonoBehaviour
{
    [SerializeField] private RoomAnchor roomAnchor;
    [SerializeField] private AprilTagRoomLocalizer localizer;
    [Tooltip("Printed tag size (black square edge) until the user sets one.")]
    [SerializeField] private float defaultTagSizeMeters = 0.0944f;

    private const string NameKey = "RoomSurvey.name";
    private const string SizeKey = "RoomSurvey.tagSizeMeters";
    private const string DataManagerKey = "RoomSurvey.dataManager";

    public string RoomName { get; private set; }
    public float TagSizeMeters { get; private set; }
    public string DataManager { get; private set; }
    public RoomAnchor Anchor => roomAnchor;

    // The last save: its config, the QR image (null without an encoder) and a
    // line for the screen.
    public RoomConfig SavedConfig { get; private set; }
    public Texture2D SavedCodeImage { get; private set; }
    public string SaveMessage { get; private set; } = "";

    private void Awake()
    {
        RoomName = PlayerPrefs.GetString(NameKey, "");
        TagSizeMeters = PlayerPrefs.GetFloat(SizeKey, defaultTagSizeMeters);
        DataManager = PlayerPrefs.GetString(DataManagerKey, "");
        if (localizer == null)
        {
            localizer = FindAnyObjectByType<AprilTagRoomLocalizer>();
        }
    }

    private IEnumerator Start()
    {
        while (!localizer.IsReady)
        {
            yield return null;
        }
        if (localizer.IsConfigLoaded)
        {
            // Extending an earlier survey: its name and size win.
            var loaded = localizer.LoadedConfig;
            if (!string.IsNullOrEmpty(loaded.name))
            {
                SetRoomName(loaded.name);
            }
            if (loaded.defaultTagSizeMeters > 0f)
            {
                StoreSize(loaded.defaultTagSizeMeters);
            }
            Debug.Log($"[RoomSurvey] Extending the saved survey {RoomCode.SurveyId(loaded)} ({loaded.tags.Length} tags)");
        }
        else
        {
            NewSurvey();
        }
    }

    // Forgets every learned tag and starts over: the next tag seen defines the room.
    public void NewSurvey()
    {
        roomAnchor.ForgetLearnedTags();
        localizer.ApplyConfig(new RoomConfig
        {
            name = RoomName,
            dataManager = DataManager,
            learnUnlistedTags = true,
            defaultTagSizeMeters = TagSizeMeters,
            tags = System.Array.Empty<TagPlacement>(),
        }, "new survey");
        SavedConfig = null;
        SavedCodeImage = null;
        SaveMessage = "";
        Debug.Log($"[RoomSurvey] New survey of '{RoomName}', tags {TagSizeMeters * 100f:F2} cm");
    }

    public void SetRoomName(string roomName)
    {
        RoomName = (roomName ?? "").Trim();
        PlayerPrefs.SetString(NameKey, RoomName);
        PlayerPrefs.Save();
        if (localizer.IsConfigLoaded)
        {
            localizer.LoadedConfig.name = RoomName;
        }
    }

    public void SetDataManager(string address)
    {
        DataManager = (address ?? "").Trim();
        PlayerPrefs.SetString(DataManagerKey, DataManager);
        PlayerPrefs.Save();
        if (localizer.IsConfigLoaded)
        {
            localizer.LoadedConfig.dataManager = DataManager;
        }
    }

    // Every position is measured in tag sizes, so a new size starts a new survey.
    // Returns false (and changes nothing) for a size that isn't 1-100 cm.
    public bool SetTagSize(float sizeMeters)
    {
        if (sizeMeters < 0.01f || sizeMeters > 1f)
        {
            return false;
        }
        if (Mathf.Abs(sizeMeters - TagSizeMeters) > 1e-5f)
        {
            StoreSize(sizeMeters);
            NewSurvey();
        }
        return true;
    }

    private void StoreSize(float sizeMeters)
    {
        TagSizeMeters = sizeMeters;
        PlayerPrefs.SetFloat(SizeKey, sizeMeters);
        PlayerPrefs.Save();
    }

    // Writes room_config.json and the room code (room_code.png/.txt) to
    // persistentDataPath. Returns false when there's nothing to save yet.
    public bool Save()
    {
        if (!roomAnchor.IsAnchored)
        {
            SaveMessage = "Not saved: the room isn't anchored yet.";
            return false;
        }
        var (path, tags) = roomAnchor.SaveSurveyedConfig();
        SavedConfig = roomAnchor.BuildSurveyedConfig();
        SavedCodeImage = RoomCodeImage.Save(SavedConfig);
        var roomName = string.IsNullOrEmpty(SavedConfig.name) ? "(unnamed)" : SavedConfig.name;
        SaveMessage = tags < 2
            ? $"Saved only {tags} tag - learn at least one more before using it."
            : $"Saved {roomName}: {tags} tags, survey {RoomCode.SurveyId(SavedConfig)}, to {System.IO.Path.GetFileName(path)}" +
              (SavedCodeImage != null ? " and room_code.png. Other devices scan the code shown here." : ".");
        return true;
    }

    // One line for a status display: the room and tag size.
    public string SettingsLine =>
        $"Room: {(string.IsNullOrEmpty(RoomName) ? "(unnamed)" : RoomName)}   Tags: {TagSizeMeters * 100f:F2} cm" +
        (string.IsNullOrEmpty(DataManager) ? "" : $"   DataManager: {DataManager}");
}
