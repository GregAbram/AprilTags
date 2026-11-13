using System.Collections;
using TMPro;
using UnityEngine;

// Shared by the Quest controls: a status label that eases toward a point in
// front of and slightly below the eyes, facing them, and controller pulses.
public static class QuestStatusPanel
{
    public static void Follow(TextMeshPro text, Transform head, float distance = 1.2f, float drop = 0.25f, float speed = 3f)
    {
        if (text == null || head == null)
        {
            return;
        }
        var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        var target = head.position + forward * distance + Vector3.down * drop;
        var t = 1f - Mathf.Exp(-speed * Time.deltaTime);
        text.transform.position = Vector3.Lerp(text.transform.position, target, t);
        text.transform.rotation = Quaternion.LookRotation(text.transform.position - head.position, Vector3.up);
    }

    public static IEnumerator Pulse(float amplitude, float seconds)
    {
        OVRInput.SetControllerVibration(1f, amplitude, OVRInput.Controller.Touch);
        yield return new WaitForSeconds(seconds);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.Touch);
    }

    // Plain log lines without stack traces: on device they fill the log buffer
    // and push out the lines worth reading. Warnings and errors keep theirs.
    public static void QuietLogs()
    {
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        CheckRendering();
    }

    // Logs the settings that decide whether dense content stays at full frame
    // rate, and warns about the two that cost the most (README: Performance on
    // Quest). Once per run.
    private static bool renderingChecked;

    public static void CheckRendering()
    {
        if (renderingChecked)
        {
            return;
        }
        renderingChecked = true;
        var msaa = QualitySettings.antiAliasing;
        var manager = Object.FindAnyObjectByType<OVRManager>();
        var max = manager != null ? Mathf.Max(manager.quest3MaxDynamicResolutionScale, manager.quest2MaxDynamicResolutionScale) : 1f;
        Debug.Log($"[QuestRendering] MSAA {(msaa > 1 ? msaa + "x" : "off")}, dynamic resolution " +
                  $"{(manager != null && manager.enableDynamicResolution ? $"up to {max:0.##}x" : "off")}");
        if (msaa > 1)
        {
            Debug.LogWarning($"[QuestRendering] MSAA {msaa}x: content that fills the view is drawn once per tile, about {msaa}x as many; " +
                             "expect dropped frames with dense meshes. The AprilTags Quest build sets it from AprilTagsAppInfo.QuestMsaa.");
        }
        if (manager != null && manager.enableDynamicResolution && max > 1f)
        {
            Debug.LogWarning($"[QuestRendering] Dynamic resolution may supersample up to {max:0.##}x, so heavy content judders until " +
                             "it scales down. Set OVRManager's Quest 3 / Quest 2 Max Dynamic Resolution Scale to 1 or less.");
        }
    }
}
