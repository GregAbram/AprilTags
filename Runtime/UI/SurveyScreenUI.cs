using UnityEngine;
using UnityEngine.SceneManagement;

// On-screen controls for a survey scene (RoomAnchor in survey mode), drawn with OnGUI:
// Scan now, Re-anchor, Start over (forget learned tags), Save (write the
// surveyed room_config.json to persistentDataPath), Scene (switch). A survey
// anchors on one tag's orientation and every learned position inherits its
// error; phone cameras measure that less consistently than Quest 3 (1-2.6 deg
// vs ~0.2-1.5 deg here), so survey with a Quest when there is one.
public class SurveyScreenUI : MonoBehaviour
{
    [SerializeField] private RoomAnchor roomAnchor;
    [Tooltip("Scene the Scene button loads; empty hides the button.")]
    [SerializeField] private string otherSceneName = "";
    [Tooltip("Shown after SURVEY in the status, e.g. a reminder that another device surveys more accurately.")]
    [SerializeField] private string note = "";

    private GUIStyle labelStyle;
    private GUIStyle shadowStyle;
    private GUIStyle buttonStyle;
    private string saveMessage = "";

    private void Awake()
    {
        // Plain log lines without stack traces: on device they fill the log and
        // push out the lines worth reading. Warnings and errors keep theirs.
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
    }

    private void OnGUI()
    {
        var unit = Mathf.Min(Screen.width, Screen.height) / 20f;
        labelStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
        shadowStyle ??= new GUIStyle(labelStyle) { normal = { textColor = Color.black } };
        buttonStyle ??= new GUIStyle(GUI.skin.button);
        labelStyle.fontSize = Mathf.RoundToInt(unit * 0.55f);
        shadowStyle.fontSize = labelStyle.fontSize;
        buttonStyle.fontSize = Mathf.RoundToInt(unit * 0.6f);

        var safe = Screen.safeArea;
        var left = safe.xMin + unit * 0.5f;
        var top = Screen.height - safe.yMax + unit * 0.5f;
        var buttonWidth = unit * 4.6f;
        var buttonHeight = unit * 1.6f;
        var step = buttonWidth + unit * 0.4f;

        if (GUI.Button(new Rect(left, top, buttonWidth, buttonHeight), "Scan now", buttonStyle))
        {
            roomAnchor.Rescan();
        }
        if (GUI.Button(new Rect(left + step, top, buttonWidth, buttonHeight), "Re-anchor", buttonStyle))
        {
            roomAnchor.ClearTags();
            saveMessage = "";
        }
        if (GUI.Button(new Rect(left + step * 2f, top, buttonWidth, buttonHeight), "Start over", buttonStyle))
        {
            roomAnchor.ForgetLearnedTags();
            saveMessage = "";
        }
        if (GUI.Button(new Rect(left + step * 3f, top, buttonWidth, buttonHeight), "Save", buttonStyle))
        {
            Save();
        }
        if (!string.IsNullOrEmpty(otherSceneName) &&
            GUI.Button(new Rect(left + step * 4f, top, buttonWidth, buttonHeight), "Scene", buttonStyle))
        {
            SceneManager.LoadScene(otherSceneName);
        }

        var text = "SURVEY" + (string.IsNullOrEmpty(note) ? "" : $" ({note})") + "\n" + roomAnchor.StatusText + saveMessage;
        var textTop = top + buttonHeight + unit * 0.3f;
        var area = new Rect(left, textTop, safe.width - unit, Screen.height - textTop);
        GUI.Label(new Rect(area.x + 2, area.y + 2, area.width, area.height), text, shadowStyle);
        GUI.Label(area, text, labelStyle);
    }

    private void Save()
    {
        if (!roomAnchor.IsAnchored)
        {
            saveMessage = "\nNot saved: the room isn't anchored yet.";
            return;
        }
        var (path, tags) = roomAnchor.SaveSurveyedConfig();
        saveMessage = tags < 2
            ? $"\nSaved only {tags} tag - learn at least one more before using it."
            : $"\nSaved {tags} tags to {System.IO.Path.GetFileName(path)}; used from the next start.";
    }
}
