using UnityEngine;

// A fixed point for checking that viewers agree: keeps this Transform (a child
// of the RoomAnchor) on the floor (room y = 0) below the center of all the
// measured tags in the config - the same point on every device, whatever tags
// each has seen. Each viewer sees it where their own device places the room;
// the distance between where two devices show it is how far they disagree.
public class RoomReferenceMarker : MonoBehaviour
{
    [SerializeField] private RoomAnchor roomAnchor;

    private AprilTagRoomLocalizer localizer;
    private bool placed;

    private void LateUpdate()
    {
        if (placed)
        {
            return;
        }
        localizer ??= FindAnyObjectByType<AprilTagRoomLocalizer>();
        if (localizer == null || !localizer.IsConfigLoaded)
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
        placed = true;
        Debug.Log($"[RoomReferenceMarker] At room ({center.x:F3}, 0, {center.z:F3}), below the center of {count} measured tags");
    }
}
