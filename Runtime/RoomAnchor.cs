using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

// Makes this GameObject's Transform the room frame: author content as its
// children in room coordinates (meters; +X right, +Y up, +Z into the front
// wall, origin as in room_config.json), and at runtime this Transform is moved
// to where the room actually is in the session. Keep this object at the scene
// root with unit scale.
//
// Built from the localizer's single-frame tag observations (see
// Documentation~/BackgroundScan.md): each tag keeps a short history of recent
// world positions; with two or more tags the room is RoomFit to their averaged
// positions (tag orientations are ignored - their yaw is too noisy beyond
// ~1 m); with one tag it is placed provisionally from that tag's own yaw. A
// lone outlying observation is dropped; several in a row mean the tracking
// frame moved, and the history restarts. The localizer's scan rate follows the
// state: fast while a visible tag needs samples, slow once the fit is stable.
public class RoomAnchor : MonoBehaviour
{
    [SerializeField] private AprilTagRoomLocalizer localizer;

    [Tooltip("Keep scanning for tags in the background (no Acquire needed).")]
    [SerializeField] private bool scanContinuously = true;

    [Tooltip("Keep child content inactive until the first placement, so it never appears at the wrong place.")]
    [SerializeField] private bool hideUntilLocalized = true;

    [Header("Scan rate (seconds between frames)")]
    [SerializeField] private float fastScanInterval = 0f;
    [SerializeField] private float scanInterval = 0.5f;
    [SerializeField] private float stableScanInterval = 3f;

    [Header("Observations")]
    [Tooltip("Observations per tag before it stops asking for fast scanning.")]
    [SerializeField] private int samplesPerTag = 8;
    [Tooltip("Most recent observations kept per tag.")]
    [SerializeField] private int historyPerTag = 30;
    [Tooltip("Observations older than this are dropped, so tracking drift ages out.")]
    [SerializeField] private float observationMaxAgeSeconds = 120f;
    [Tooltip("An observation this far (m) from its tag's average is an outlier.")]
    [SerializeField] private float outlierDistance = 0.10f;
    [Tooltip("This many outliers in a row on a tag mean the tracking frame moved: restart the history.")]
    [SerializeField] private int outliersForReset = 3;
    [Tooltip("RoomFit RMS residual (m) below which a fit of 2+ well-sampled tags counts as stable.")]
    [SerializeField] private float stableRmsResidual = 0.05f;

    [Tooltip("Seconds for the displayed pose to ease toward a new estimate (0 = jump).")]
    [SerializeField] private float smoothingSeconds = 0.3f;

    [Tooltip("Config drawn as Scene-view gizmos (read from StreamingAssets in the editor only).")]
    [SerializeField] private string gizmoConfigFileName = "room_config.json";

    // Raised after every pose update.
    public event Action<RoomAnchor> Localized;

    public bool IsLocalized { get; private set; }
    // True while the pose comes from a single tag's own (noisy) yaw.
    public bool IsProvisional { get; private set; }
    public bool IsStable { get; private set; }
    public int TagCount => histories.Count(h => h.Value.Observations.Count > 0);
    public RoomFitResult? LastFit { get; private set; }

    private sealed class TagHistory
    {
        public readonly List<TagObservation> Observations = new();
        public readonly List<TagObservation> Outliers = new();
        public Vector3 MeanWorldPosition;
        public float MeanDistance;

        public void Recompute()
        {
            var sum = Vector3.zero;
            var distance = 0f;
            foreach (var o in Observations)
            {
                sum += o.WorldPosition;
                distance += o.Distance;
            }
            MeanWorldPosition = Observations.Count > 0 ? sum / Observations.Count : Vector3.zero;
            MeanDistance = Observations.Count > 0 ? distance / Observations.Count : 0f;
        }
    }

    private readonly SortedDictionary<int, TagHistory> histories = new();
    private Vector3 targetPosition;
    private Quaternion targetRotation = Quaternion.identity;
    private bool snapNext = true;
    private int lastLoggedTagCount;
    private bool lastLoggedProvisional;
    private bool lastLoggedStable;

    // Looks for a tag at full rate until one locks; its frames refine the room
    // like any others. Safe to call any time.
    public void Rescan()
    {
        if (localizer != null && !localizer.IsAcquiring)
        {
            localizer.BeginAcquisition();
        }
    }

    // Forgets all observations; the pose stays where it is until the next one.
    public void ClearTags()
    {
        histories.Clear();
        LastFit = null;
        IsStable = false;
        snapNext = true;
        UpdateScanRate();
    }

    // One-line state plus a line per tag, for an on-screen or in-headset display.
    public string StatusText
    {
        get
        {
            var text = new StringBuilder();
            if (!IsLocalized)
            {
                text.Append("Room: looking for a tag");
            }
            else if (IsProvisional)
            {
                text.Append("Room: provisional, from one tag - look at a second tag to fix it");
            }
            else if (LastFit.HasValue)
            {
                text.Append($"Room: fit from {LastFit.Value.Residuals.Count} tags, RMS {LastFit.Value.RmsResidual * 100f:F1} cm{(IsStable ? ", stable" : "")}");
            }
            if (localizer != null)
            {
                text.Append($"   scan every {localizer.ScanIntervalSeconds:F1}s");
            }
            text.AppendLine();
            foreach (var (tagId, history) in histories)
            {
                if (history.Observations.Count == 0)
                {
                    continue;
                }
                var age = Time.time - history.Observations[^1].Time;
                text.Append($"  tag {tagId}: {history.Observations.Count} obs, {history.MeanDistance:F1} m, last {age:F0}s ago");
                if (LastFit.HasValue)
                {
                    foreach (var (id, residual) in LastFit.Value.Residuals)
                    {
                        if (id == tagId)
                        {
                            text.Append($", residual {residual.magnitude * 100f:F1} cm");
                        }
                    }
                }
                text.AppendLine();
            }
            return text.ToString();
        }
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
            localizer.TagObserved += OnTagObserved;
        }
    }

    private void OnDisable()
    {
        if (localizer != null)
        {
            localizer.TagObserved -= OnTagObserved;
        }
    }

    private void Start()
    {
        localizer.ContinuousScan = scanContinuously;
        UpdateScanRate();
    }

    private void Update()
    {
        // Also when no tag is in view: drop back from fast scanning once the
        // tag that wanted samples has gone.
        UpdateScanRate();
        if (!IsLocalized)
        {
            return;
        }
        if (smoothingSeconds <= 0f)
        {
            transform.SetPositionAndRotation(targetPosition, targetRotation);
            return;
        }
        var t = 1f - Mathf.Exp(-Time.deltaTime / smoothingSeconds);
        transform.SetPositionAndRotation(
            Vector3.Lerp(transform.position, targetPosition, t),
            Quaternion.Slerp(transform.rotation, targetRotation, t));
    }

    private void OnTagObserved(TagObservation observation)
    {
        if (!histories.TryGetValue(observation.TagId, out var history))
        {
            history = new TagHistory();
            histories[observation.TagId] = history;
        }

        if (history.Observations.Count >= 3 &&
            Vector3.Distance(observation.WorldPosition, history.MeanWorldPosition) > outlierDistance)
        {
            history.Outliers.Add(observation);
            if (history.Outliers.Count < outliersForReset)
            {
                return;
            }

            // Consistently somewhere else: the tracking frame moved (recenter,
            // relocalization), or the tag did. Start over from the new sightings.
            var moved = Vector3.Distance(observation.WorldPosition, history.MeanWorldPosition);
            Debug.Log($"[RoomAnchor] Tag {observation.TagId} seen {moved * 100f:F0} cm from its average {history.Outliers.Count} times in a row; tracking frame moved - restarting from new observations");
            var fresh = new List<TagObservation>(history.Outliers);
            histories.Clear();
            history = new TagHistory();
            history.Observations.AddRange(fresh);
            histories[observation.TagId] = history;
            LastFit = null;
            IsStable = false;
            snapNext = true;
        }
        else
        {
            history.Outliers.Clear();
            history.Observations.Add(observation);
        }

        if (history.Observations.Count > historyPerTag)
        {
            history.Observations.RemoveRange(0, history.Observations.Count - historyPerTag);
        }
        Refit();
    }

    private void Refit()
    {
        var cutoff = Time.time - observationMaxAgeSeconds;
        foreach (var history in histories.Values)
        {
            history.Observations.RemoveAll(o => o.Time < cutoff);
            history.Recompute();
        }

        var seen = histories.Where(h => h.Value.Observations.Count > 0).ToList();
        if (seen.Count == 0)
        {
            return;
        }

        var correspondences = seen
            .Select(h => new TagCorrespondence(h.Key, h.Value.Observations[0].TagRoomPosition, h.Value.MeanWorldPosition))
            .ToList();

        if (RoomFit.TryFit(correspondences, out var fit))
        {
            LastFit = fit;
            IsProvisional = false;
            targetPosition = fit.Position;
            targetRotation = fit.Rotation;
            IsStable = fit.RmsResidual <= stableRmsResidual &&
                       seen.Count(h => h.Value.Observations.Count >= samplesPerTag) >= 2;
        }
        else
        {
            // One tag (or tags too close together for a yaw): use the tag seen
            // closest, its yaw averaged with close-range observations weighted
            // most (single-frame yaw error grows quickly with distance).
            var (tagId, history) = seen.OrderBy(h => h.Value.MeanDistance).First();
            var forward = Vector3.zero;
            foreach (var o in history.Observations)
            {
                forward += o.OriginRotation * Vector3.forward / Mathf.Max(o.Distance * o.Distance, 0.01f);
            }
            var rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, Vector3.up), Vector3.up);
            LastFit = null;
            IsProvisional = true;
            IsStable = false;
            targetRotation = rotation;
            targetPosition = history.MeanWorldPosition - rotation * history.Observations[0].TagRoomPosition;
        }

        if (!IsLocalized || snapNext)
        {
            transform.SetPositionAndRotation(targetPosition, targetRotation);
            snapNext = false;
        }
        if (!IsLocalized)
        {
            IsLocalized = true;
            if (hideUntilLocalized)
            {
                SetChildrenActive(true);
            }
        }

        LogStateChange(seen.Count);
        UpdateScanRate();
        Localized?.Invoke(this);
    }

    private void LogStateChange(int tagCount)
    {
        if (tagCount == lastLoggedTagCount && IsProvisional == lastLoggedProvisional && IsStable == lastLoggedStable)
        {
            return;
        }
        lastLoggedTagCount = tagCount;
        lastLoggedProvisional = IsProvisional;
        lastLoggedStable = IsStable;
        if (LastFit.HasValue)
        {
            var residuals = string.Join(", ", LastFit.Value.Residuals
                .OrderBy(r => r.tagId)
                .Select(r => $"tag {r.tagId} {r.residual.magnitude * 100f:F1}"));
            Debug.Log($"[RoomAnchor] Fit from {tagCount} tags{(IsStable ? " (stable)" : "")}: RMS residual {LastFit.Value.RmsResidual * 100f:F1} cm ({residuals} cm); origin {targetPosition} yaw {targetRotation.eulerAngles.y:F1}");
        }
        else
        {
            Debug.Log($"[RoomAnchor] Provisional placement from one tag ({tagCount} seen); origin {targetPosition} yaw {targetRotation.eulerAngles.y:F1}");
        }
    }

    // Fast while a recently seen tag still needs samples; slow once stable.
    private void UpdateScanRate()
    {
        if (localizer == null)
        {
            return;
        }
        var recent = Time.time - 2f;
        var needsSamples = histories.Values.Any(h =>
            h.Observations.Count > 0 && h.Observations.Count < samplesPerTag && h.Observations[^1].Time >= recent);
        localizer.ScanIntervalSeconds = needsSamples ? fastScanInterval : IsStable ? stableScanInterval : scanInterval;
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
