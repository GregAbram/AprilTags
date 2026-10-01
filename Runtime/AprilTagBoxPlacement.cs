using Meta.XR;
using UnityEngine;

// Places one box at a fixed room-frame pose using the first tag that locks after
// startup. Detection lives in AprilTagRoomLocalizer: an existing one on this
// GameObject is used as-is, otherwise one is added and configured from the
// fields below (kept so scenes made before the split keep working).
public class AprilTagBoxPlacement : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess cameraAccess;
    [SerializeField] private Transform boxTransform;
    [SerializeField] private Vector3 boxRoomPosition = Vector3.zero;
    [SerializeField] private float boxRoomYawDegrees = 0f;
    [SerializeField] private int quadDecimate = 2;
    [SerializeField] private string configFileName = "room_config.json";
    [SerializeField] private float trackingSettleDelaySeconds = 2f;
    [SerializeField] private float acquisitionWindowSeconds = 1f;
    [SerializeField] private float maxSampleGapSeconds = 0.25f;
    [SerializeField] private float maxOffAxisAngleDegrees = 20f;

    private AprilTagRoomLocalizer localizer;

    private void Awake()
    {
        localizer = GetComponent<AprilTagRoomLocalizer>();
        if (localizer == null)
        {
            localizer = gameObject.AddComponent<AprilTagRoomLocalizer>();
            localizer.Configure(cameraAccess, quadDecimate, configFileName,
                trackingSettleDelaySeconds, acquisitionWindowSeconds, maxSampleGapSeconds, maxOffAxisAngleDegrees);
        }

        localizer.EstimateAcquired += OnEstimateAcquired;
        localizer.BeginAcquisition();
    }

    private void OnEstimateAcquired(RoomOriginEstimate estimate)
    {
        localizer.EstimateAcquired -= OnEstimateAcquired;

        var boxWorldPosition = estimate.RoomToWorld(boxRoomPosition);
        var boxWorldRotation = estimate.Rotation * Quaternion.Euler(0, boxRoomYawDegrees, 0);
        boxTransform.SetPositionAndRotation(boxWorldPosition, boxWorldRotation);

        Debug.Log($"[AprilTagBoxPlacement] Box placed at world pos={boxWorldPosition} rot={boxWorldRotation.eulerAngles}");
    }

    private void OnDestroy()
    {
        if (localizer != null)
        {
            localizer.EstimateAcquired -= OnEstimateAcquired;
        }
    }
}
