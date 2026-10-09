using System;
using UnityEngine;

// Sets the room from a QR room code (RoomCode) seen by the camera, and
// remembers it: the decoded config is applied and saved as this device's room
// (persistentDataPath), so later starts use it without scanning again. Scans
// by itself while the device has no room; BeginScan() scans for a new one
// (e.g. a "Scan room code" button). Codes of the same survey, and QR codes that
// aren't room codes, are ignored. Needs a decoder: the TaccAprilTags.RoomCode
// assembly (ZXing) installs one.
public class RoomCodeReader : MonoBehaviour
{
    [SerializeField] private AprilTagRoomLocalizer localizer;

    public event Action<RoomConfig> RoomChanged;

    public bool IsScanning => localizer != null && localizer.ScanForRoomCode;
    public bool HasDecoder => localizer != null && (localizer.RoomCodeDecoder ?? AprilTagRoomLocalizer.DefaultRoomCodeDecoder) != null;

    // "Room: TACC lab (survey 3KX9QM)", or what to do when there is none.
    public string StatusLine
    {
        get
        {
            if (localizer == null)
            {
                return "";
            }
            var scanning = IsScanning ? " - scanning for a room code" : "";
            if (!localizer.IsConfigLoaded)
            {
                return HasDecoder ? "No room yet: point the camera at the room's QR code" : "No room config and no QR decoder";
            }
            var config = localizer.LoadedConfig;
            return $"Room: {(string.IsNullOrEmpty(config.name) ? "(unnamed)" : config.name)} (survey {RoomCode.SurveyId(config)}){scanning}";
        }
    }

    public void BeginScan()
    {
        if (localizer != null)
        {
            localizer.ScanForRoomCode = true;
            Debug.Log("[RoomCodeReader] Scanning for a room code");
        }
    }

    public void CancelScan()
    {
        if (localizer != null)
        {
            localizer.ScanForRoomCode = false;
        }
    }

    private void Awake()
    {
        if (localizer == null)
        {
            localizer = FindAnyObjectByType<AprilTagRoomLocalizer>();
        }
    }

    private void OnEnable()
    {
        if (localizer != null)
        {
            localizer.RoomCodeFound += OnRoomCodeFound;
        }
    }

    private void OnDisable()
    {
        if (localizer != null)
        {
            localizer.RoomCodeFound -= OnRoomCodeFound;
        }
    }

    private void Update()
    {
        // No room at all once the localizer is up: look for a code.
        if (localizer != null && localizer.IsReady && !localizer.IsConfigLoaded && !localizer.ScanForRoomCode && HasDecoder)
        {
            BeginScan();
        }
    }

    private void OnRoomCodeFound(string text)
    {
        var config = RoomCode.Decode(text);
        if (config == null)
        {
            Debug.Log("[RoomCodeReader] Ignoring a QR code that isn't a room code");
            return;
        }
        localizer.ScanForRoomCode = false;
        var surveyId = RoomCode.SurveyId(config);
        // Same tags, name and DataManager: nothing to do. A code that only renames
        // the room or changes its DataManager still applies.
        if (localizer.IsConfigLoaded && RoomCode.Encode(localizer.LoadedConfig) == RoomCode.Encode(config))
        {
            Debug.Log($"[RoomCodeReader] Already in this room (survey {surveyId})");
            return;
        }
        localizer.ApplyConfig(config, "room code");
        localizer.SaveConfigOverride(config);
        Debug.Log($"[RoomCodeReader] Room set from code: {(string.IsNullOrEmpty(config.name) ? "(unnamed)" : config.name)}, survey {surveyId}, {config.tags.Length} tags");
        RoomChanged?.Invoke(config);
    }
}
