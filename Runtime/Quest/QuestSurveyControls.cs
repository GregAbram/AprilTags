using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

// Quest controls for a survey (RoomSurvey + RoomAnchor in survey mode):
// A = scan now, B = re-anchor, X twice = new survey (forget everything),
// right trigger = save room_config.json and the room code, left trigger = edit
// the room name, left grip = edit the tag size (cm), Y = switch scene (when
// one is set). Text is typed on the system keyboard, which needs "Requires
// System Keyboard" in the Meta project config (the app setup sets it).
//
// Nobody else can scan a code shown inside the headset, so the saved
// room_code.png stays on the Quest for printing: adb pull it from the app's
// files folder (the status shows the path).
public class QuestSurveyControls : MonoBehaviour
{
    [SerializeField] private RoomSurvey roomSurvey;
    [SerializeField] private TextMeshPro statusText;
    [SerializeField] private Transform head;
    [Tooltip("Scene Y loads; empty = no scene switch.")]
    [SerializeField] private string otherSceneName = "";
    [Tooltip("Shown above the controls, e.g. the app's name.")]
    [SerializeField] private string title = "";

    private enum Editing { None, Name, Size }

    private TouchScreenKeyboard keyboard;
    private Editing editing;
    private float newSurveyArmedUntil = -1f;
    private string message = "";

    private RoomAnchor Anchor => roomSurvey.Anchor;

    private void Awake() => QuestStatusPanel.QuietLogs();

    private void Update()
    {
        if (editing != Editing.None)
        {
            PollKeyboard();
        }
        else
        {
            HandleButtons();
        }

        var help = editing switch
        {
            Editing.Name => "Type the room name, then Done",
            Editing.Size => "Type the printed tag size in cm (the black square's edge), then Done",
            _ => "A: scan now   B: re-anchor   X twice: new survey   R trigger: save\n" +
                 "L trigger: room name   L grip: tag size" + (string.IsNullOrEmpty(otherSceneName) ? "" : "   Y: other scene"),
        };
        statusText.text = (string.IsNullOrEmpty(title) ? "SURVEY" : title) + "\n" + help + "\n" +
                          roomSurvey.SettingsLine + "\n" + Anchor.StatusText +
                          (string.IsNullOrEmpty(message) ? "" : "\n" + message);
        QuestStatusPanel.Follow(statusText, head);
    }

    private void HandleButtons()
    {
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            Anchor.Rescan();
            Pulse();
        }
        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            Anchor.ClearTags();
            message = "";
            Pulse();
        }
        if (OVRInput.GetDown(OVRInput.Button.Three))
        {
            if (Time.time < newSurveyArmedUntil)
            {
                roomSurvey.NewSurvey();
                newSurveyArmedUntil = -1f;
                message = "New survey: the next tag you look at defines the room.";
                StartCoroutine(QuestStatusPanel.Pulse(1f, 0.15f));
            }
            else
            {
                newSurveyArmedUntil = Time.time + 2f;
                message = "Press X again within 2 s to forget this survey and start a new one.";
                Pulse();
            }
        }
        if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
        {
            var saved = roomSurvey.Save();
            message = roomSurvey.SaveMessage +
                      (saved ? $"\nFiles: {Path.Combine(Application.persistentDataPath, "room_code.png")}" : "");
            StartCoroutine(saved ? QuestStatusPanel.Pulse(1f, 0.15f) : QuestStatusPanel.Pulse(0.3f, 0.05f));
        }
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
        {
            OpenKeyboard(Editing.Name, roomSurvey.RoomName);
        }
        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger))
        {
            OpenKeyboard(Editing.Size, (roomSurvey.TagSizeMeters * 100f).ToString("0.##"));
        }
        if (!string.IsNullOrEmpty(otherSceneName) && OVRInput.GetDown(OVRInput.Button.Four))
        {
            SceneManager.LoadScene(otherSceneName);
        }
    }

    private void OpenKeyboard(Editing what, string text)
    {
        keyboard = TouchScreenKeyboard.Open(text,
            what == Editing.Size ? TouchScreenKeyboardType.DecimalPad : TouchScreenKeyboardType.Default);
        if (keyboard == null)
        {
            message = "No system keyboard here (it needs \"Requires System Keyboard\" in the Meta project config).";
            return;
        }
        editing = what;
        message = "";
        Pulse();
    }

    private void PollKeyboard()
    {
        if (keyboard == null)
        {
            editing = Editing.None;
            return;
        }
        if (keyboard.status == TouchScreenKeyboard.Status.Visible)
        {
            return;
        }
        if (keyboard.status == TouchScreenKeyboard.Status.Done)
        {
            Apply(editing, keyboard.text);
        }
        keyboard = null;
        editing = Editing.None;
    }

    private void Apply(Editing what, string text)
    {
        if (what == Editing.Name)
        {
            roomSurvey.SetRoomName(text);
            message = "";
            return;
        }
        var before = roomSurvey.TagSizeMeters;
        if (!float.TryParse(text, out var cm) || !roomSurvey.SetTagSize(cm / 100f))
        {
            message = $"'{text}' isn't a tag size in cm (1-100).";
        }
        else if (Mathf.Abs(before - roomSurvey.TagSizeMeters) > 1e-5f)
        {
            message = "New tag size: the survey started over.";
        }
    }

    private void Pulse() => StartCoroutine(QuestStatusPanel.Pulse(0.3f, 0.05f));
}
