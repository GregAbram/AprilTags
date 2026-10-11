using UnityEngine;

// A fixed point for checking that viewers agree: keeps this Transform (a child
// of the RoomAnchor) at room height 0 below the center of all the measured
// tags in the config - the same point on every device, whatever tags each has
// seen. Height 0 is the floor in a hand-measured config, and the first tag's
// center in a surveyed one. Placed again when the room changes. Each viewer sees it where their own device places the room;
// the distance between where two devices show it is how far they disagree.
public class RoomReferenceMarker : MonoBehaviour
{
    [SerializeField] private RoomAnchor roomAnchor;

    private AprilTagRoomLocalizer localizer;
    private RoomConfig placedFor;

    private void LateUpdate()
    {
        localizer ??= FindAnyObjectByType<AprilTagRoomLocalizer>();
        if (localizer == null || !localizer.IsConfigLoaded || localizer.LoadedConfig == placedFor)
        {
            return;
        }

        var sum = Vector3.zero;
        var count = 0;
        foreach (var tag in localizer.LoadedConfig.tags)
        {
            if (localizer.TryGetConfiguredTag(tag.id, out var position, out _, out var measured) && measured)
            {
                sum += position;
                count++;
            }
        }
        if (count == 0)
        {
            return;
        }
        var center = sum / count;
        transform.localPosition = new Vector3(center.x, 0f, center.z);
        transform.localRotation = Quaternion.identity;
        placedFor = localizer.LoadedConfig;
        Debug.Log($"[RoomReferenceMarker] At room ({center.x:F3}, 0, {center.z:F3}), below the center of {count} measured tags");
    }
}
