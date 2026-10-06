using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Makes this GameObject's Transform the room frame: author content as its
// children in room coordinates (meters; +X right, +Y up, +Z into the front
// wall, origin as in room_config.json), and at runtime this Transform is moved
// to where the room actually is in the session. Every tag lock is kept and the
// pose refit from all tags seen so far (RoomFit, positions only); with a single
// tag it falls back to that tag's own orientation. Keep this object at the
// scene root with unit scale.
public class RoomAnchor : MonoBehaviour
{
    [SerializeField] private AprilTagRoomLocalizer localizer;

    [Tooltip("Start looking for a tag as soon as the scene starts.")]
    [SerializeField] private bool acquireOnStart = true;

    [Tooltip("Keep child content inactive until the first lock, so it never appears at the wrong place.")]
    [SerializeField] private bool hideUntilLocalized = true;

    [Tooltip("Config drawn as Scene-view gizmos (read from StreamingAssets in the editor only).")]
    [SerializeField] private string gizmoConfigFileName = "room_config.json";

    // Raised after every pose update, with how many tags the pose is based on.
    public event Action<RoomAnchor> Localized;

    public bool IsLocalized { get; private set; }
    public int TagCount => latestEstimates.Count;
    public RoomFitResult? LastFit { get; private set; }

    private readonly Dictionary<int, RoomOriginEstimate> latestEstimates = new();

    // Looks for a tag; the next lock is added to the fit. Safe to call any time.
    public void Rescan()
    {
        if (localizer != null && !localizer.IsAcquiring)
        {
            localizer.BeginAcquisition();
        }
    }

    // Forgets all tags seen so far; the pose stays where it is until the next lock.
    public void ClearTags()
    {
        latestEstimates.Clear();
        LastFit = null;
    }

    private void Awake()
    {
        if (localizer == null)
        {
            localizer = FindAnyObjectByType<AprilTagRoomLocalizer>();
        }
        if (localizer == null)
        {
            Debug.LogError("[RoomAnchor] No AprilTagRoomLocalizer assigned or found in the scene");
            enabled = false;
            return;
        }

        if (hideUntilLocalized)
        {
            SetChildrenActive(false);
        }
    }

    private void OnEnable()
    {
        if (localizer != null)
        {
            localizer.EstimateAcquired += OnEstimateAcquired;
        }
    }

    private void OnDisable()
    {
        if (localizer != null)
        {
            localizer.EstimateAcquired -= OnEstimateAcquired;
        }
    }

    private void Start()
    {
        if (acquireOnStart)
        {
            Rescan();
        }
    }

    private void OnEstimateAcquired(RoomOriginEstimate estimate)
    {
        latestEstimates[estimate.TagId] = estimate;

        var correspondences = latestEstimates.Values
            .Select(e => new TagCorrespondence(e.TagId, e.TagRoomPosition, e.TagWorldPosition))
            .ToList();

        if (RoomFit.TryFit(correspondences, out var fit))
        {
            LastFit = fit;
            transform.SetPositionAndRotation(fit.Position, fit.Rotation);

            var residuals = string.Join(", ", fit.Residuals
                .OrderBy(r => r.tagId)
                .Select(r => $"tag {r.tagId} {r.residual.magnitude * 100f:F1}"));
            Debug.Log($"[RoomAnchor] Fit from {correspondences.Count} tags: RMS residual {fit.RmsResidual * 100f:F1} cm ({residuals} cm)");
        }
        else
        {
            // One tag (or tags too close together): use the latest tag's own pose.
            LastFit = null;
            transform.SetPositionAndRotation(estimate.Position, estimate.Rotation);
            Debug.Log($"[RoomAnchor] Placed from tag {estimate.TagId} alone ({correspondences.Count} tag(s) seen); scan another tag to refine");
        }

        if (!IsLocalized)
        {
            IsLocalized = true;
            if (hideUntilLocalized)
            {
                SetChildrenActive(true);
            }
        }

        Localized?.Invoke(this);
    }

    private void SetChildrenActive(bool active)
    {
        foreach (Transform child in transform)
        {
            child.gameObject.SetActive(active);
        }
    }

#if UNITY_EDITOR
    // Scene-view picture of the room: each configured tag as its black-border
    // square (plus the printed outline), with a line showing the direction you
    // face to read it. Lets content be placed against the real walls.
    private RoomConfig gizmoConfig;
    private DateTime gizmoConfigTime;

    private void OnDrawGizmos()
    {
        var config = LoadGizmoConfig();
        if (config?.tags == null)
        {
            return;
        }

        var previousMatrix = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

        // Room origin axes.
        Gizmos.color = Color.red;
        Gizmos.DrawLine(Vector3.zero, Vector3.right * 0.3f);
        Gizmos.color = Color.green;
        Gizmos.DrawLine(Vector3.zero, Vector3.up * 0.3f);
        Gizmos.color = Color.blue;
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.3f);

        foreach (var tag in config.tags)
        {
            var center = new Vector3(tag.x, tag.y, tag.z);
            var rotation = Quaternion.Euler(0f, tag.yawDegrees, 0f);
            var right = rotation * Vector3.right;
            var up = Vector3.up;
            var facing = rotation * Vector3.forward;

            Gizmos.color = Color.yellow;
            DrawSquare(center, right, up, tag.sizeMeters);
            Gizmos.color = new Color(1f, 1f, 0f, 0.4f);
            DrawSquare(center, right, up, tag.sizeMeters * 9f / 5f);

            // Viewer side: you stand here and look along `facing` to read it.
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(center - facing * 0.4f, center);

            UnityEditor.Handles.Label(transform.TransformPoint(center + Vector3.up * tag.sizeMeters), $"tag {tag.id}");
        }

        Gizmos.matrix = previousMatrix;
    }

    private static void DrawSquare(Vector3 center, Vector3 right, Vector3 up, float size)
    {
        var r = right * size * 0.5f;
        var u = up * size * 0.5f;
        Gizmos.DrawLine(center - r - u, center + r - u);
        Gizmos.DrawLine(center + r - u, center + r + u);
        Gizmos.DrawLine(center + r + u, center - r + u);
        Gizmos.DrawLine(center - r + u, center - r - u);
    }

    private RoomConfig LoadGizmoConfig()
    {
        var path = Path.Combine(Application.streamingAssetsPath, gizmoConfigFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var writeTime = File.GetLastWriteTimeUtc(path);
        if (gizmoConfig == null || writeTime != gizmoConfigTime)
        {
            gizmoConfigTime = writeTime;
            try
            {
                gizmoConfig = JsonUtility.FromJson<RoomConfig>(File.ReadAllText(path));
            }
            catch (ArgumentException)
            {
                gizmoConfig = null;
            }
        }
        return gizmoConfig;
    }
#endif
}
