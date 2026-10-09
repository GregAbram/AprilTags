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
    public static void QuietLogs() => Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
}
