using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

// Quest controls for a RoomAnchor scene (a locator): A = scan at full rate now,
// B = forget this session's observations (re-anchor), X = forget learned tag
// positions, left trigger = scan a room code (QR), Y = switch scene (when one
// is set). The status floats in front of the headset; a firm pulse when the
// room becomes anchored.
public class QuestRoomControls : MonoBehaviour
{
    [SerializeField] private RoomAnchor roomAnchor;
    [Tooltip("Optional: the left trigger scans a room code, and the status shows the room.")]
    [SerializeField] private RoomCodeReader roomCodeReader;
    [SerializeField] private TextMeshPro statusText;
    [SerializeField] private Transform head;
    [Tooltip("Scene Y loads; empty = no scene switch.")]
    [SerializeField] private string otherSceneName = "";
    [Tooltip("Shown above the controls, e.g. the app's name.")]
    [SerializeField] private string title = "";

    private bool wasProvisional = true;

    private void Awake() => QuestStatusPanel.QuietLogs();

    private void OnEnable() => roomAnchor.Localized += OnLocalized;

    private void OnDisable() => roomAnchor.Localized -= OnLocalized;

    private void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            roomAnchor.Rescan();
            StartCoroutine(QuestStatusPanel.Pulse(0.3f, 0.05f));
        }
        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            roomAnchor.ClearTags();
            StartCoroutine(QuestStatusPanel.Pulse(0.3f, 0.05f));
        }
        if (OVRInput.GetDown(OVRInput.Button.Three))
        {
            roomAnchor.ForgetLearnedTags();
            StartCoroutine(QuestStatusPanel.Pulse(0.3f, 0.05f));
        }
        if (roomCodeReader != null && OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
        {
            if (roomCodeReader.IsScanning)
            {
                roomCodeReader.CancelScan();
            }
            else
            {
                roomCodeReader.BeginScan();
            }
            StartCoroutine(QuestStatusPanel.Pulse(0.3f, 0.05f));
        }
        if (!string.IsNullOrEmpty(otherSceneName) && OVRInput.GetDown(OVRInput.Button.Four))
        {
            SceneManager.LoadScene(otherSceneName);
        }

        statusText.text = (string.IsNullOrEmpty(title) ? "" : title + "\n") +
                          "A: scan now   B: re-anchor   X: forget learned" +
                          (roomCodeReader != null ? "   L trigger: room code" : "") +
                          (string.IsNullOrEmpty(otherSceneName) ? "" : "   Y: other scene") + "\n" +
                          (roomCodeReader != null ? roomCodeReader.StatusLine + "\n" : "") + roomAnchor.StatusText;
        QuestStatusPanel.Follow(statusText, head);
    }

    private void OnLocalized(RoomAnchor anchor)
    {
        if (wasProvisional && anchor.IsAnchored)
        {
            StartCoroutine(QuestStatusPanel.Pulse(1f, 0.15f));
        }
        wasProvisional = !anchor.IsAnchored;
    }
}
