using UnityEngine;

// Put one in an app's first scene: installs the QR room-code decoder and
// encoder (ZXingRoomCode.Install) and, with a file name, starts the session log
// in persistentDataPath. A scene reference is what keeps this working on
// device: a [RuntimeInitializeOnLoadMethod] hook alone in a package assembly
// was not reliably run there.
[DefaultExecutionOrder(-1000)]
public class AprilTagsAppStartup : MonoBehaviour
{
    [Tooltip("Session log in persistentDataPath (empty = no log file).")]
    [SerializeField] private string logFileName = "session.log";

    private void Awake()
    {
        if (!string.IsNullOrEmpty(logFileName))
        {
            SessionLogFile.Begin(logFileName);
        }
        ZXingRoomCode.Install();
    }
}
