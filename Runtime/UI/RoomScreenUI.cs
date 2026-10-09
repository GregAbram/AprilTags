using UnityEngine;
using UnityEngine.SceneManagement;

// On-screen controls for a RoomAnchor scene (phones, editor), drawn with OnGUI:
// Scan now (full rate until a lock), Re-anchor (forget this session's
// observations), Forget learned (all learned tag positions), Scene (switch),
// and the anchor's status below them.
public class RoomScreenUI : MonoBehaviour
{
    [SerializeField] private RoomAnchor roomAnchor;
    [Tooltip("Scene the Scene button loads; empty hides the button.")]
    [SerializeField] private string otherSceneName = "";

    private GUIStyle labelStyle;
    private GUIStyle shadowStyle;
    private GUIStyle buttonStyle;

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
        var buttonWidth = unit * 5.5f;
        var buttonHeight = unit * 1.6f;
        var step = buttonWidth + unit * 0.5f;

        if (GUI.Button(new Rect(left, top, buttonWidth, buttonHeight), "Scan now", buttonStyle))
        {
            roomAnchor.Rescan();
        }
        if (GUI.Button(new Rect(left + step, top, buttonWidth, buttonHeight), "Re-anchor", buttonStyle))
        {
            roomAnchor.ClearTags();
        }
        if (GUI.Button(new Rect(left + step * 2f, top, buttonWidth, buttonHeight), "Forget learned", buttonStyle))
        {
            roomAnchor.ForgetLearnedTags();
        }
        if (!string.IsNullOrEmpty(otherSceneName) &&
            GUI.Button(new Rect(left + step * 3f, top, buttonWidth, buttonHeight), "Scene", buttonStyle))
        {
            SceneManager.LoadScene(otherSceneName);
        }

        var text = roomAnchor.StatusText;
        var textTop = top + buttonHeight + unit * 0.3f;
        var area = new Rect(left, textTop, safe.width - unit, Screen.height - textTop);
        GUI.Label(new Rect(area.x + 2, area.y + 2, area.width, area.height), text, shadowStyle);
        GUI.Label(area, text, labelStyle);
    }
}
