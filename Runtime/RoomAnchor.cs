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
// root with unit scale. See Documentation~/BackgroundScan.md.
//
// Only tags marked "measured" in the config define the room frame, so every
// device places the room from the same surveyed reference. The room is
// anchored by two or more measured tags (RoomFit on their positions), or by one
// measured tag seen up close (single-tag yaw is only accurate within ~1 m).
// Before that it is placed provisionally from whatever measured tag is in view.
//
// Once anchored, every other tag (unmeasured, or unlisted when the config sets
// learnUnlistedTags) has its room position learned from its sightings and saved
// to learned_tags.json in persistentDataPath. Established learned tags then
// help hold the room in place (drift) with less weight than measured ones; with
// one tag in view only its position is used, never its yaw. Repeated
// disagreement by a measured or established tag means the tracking frame moved
// (recenter, relocalization): the room is re-anchored. A learned tag that keeps
// disagreeing is unlearned - it never moves the room.
//
// Survey mode (surveyMode) sets a room up: the first tag seen that has a pose in
// the config becomes the anchor (or, with no tags in the config, the first tag
// seen defines the room: its center is the origin and its facing sets the
// axes); every other tag - listed or not - is learned relative to it, position
// and yaw. SaveSurveyedConfig() then writes a room_config.json listing them all
// as measured, so every device anchors from the same tag positions.
public class RoomAnchor : MonoBehaviour
{
    [SerializeField] private AprilTagRoomLocalizer localizer;

    [Tooltip("Set up a room: anchor on the first configured tag seen, learn all others, then SaveSurveyedConfig().")]
    [SerializeField] private bool surveyMode;

    [Tooltip("Keep scanning for tags in the background (no Acquire needed).")]
    [SerializeField] private bool scanContinuously = true;

    [Tooltip("Keep child content inactive until the room is anchored, so it never appears at a provisional place.")]
    [SerializeField] private bool hideUntilLocalized = true;

    [Header("Anchoring")]
    [Tooltip("With two or more measured tags configured, anchor only by fitting two or more tags' positions: a single tag's yaw varies by device and viewpoint (1-3 deg), so viewers anchored on one tag disagree.")]
    [SerializeField] private bool requireTwoTagsWhenAvailable = true;
    [Tooltip("A single measured tag anchors the room only from observations this close (m); single-frame yaw is poor farther away.")]
    [SerializeField] private float anchorMaxDistance = 1.2f;
    [Tooltip("Close-range observations of a single measured tag needed to anchor.")]
    [SerializeField] private int anchorSamples = 8;

    [Header("Learning")]
    [Tooltip("Sightings before a learned tag is shown and helps hold the room.")]
    [SerializeField] private int establishedSamples = 20;
    [Tooltip("RoomFit weight of an established learned tag (measured tags weigh 1).")]
    [SerializeField] private float learnedTagWeight = 0.5f;
    [Tooltip("This many disagreeing sightings in a row unlearn a learned tag.")]
    [SerializeField] private int disagreementsToUnlearn = 10;
    [Tooltip("Only learn tags mounted upright (normal within this many degrees of horizontal); a tag lying on a table is ignored.")]
    [SerializeField] private float maxTagTiltDegrees = 30f;
    [SerializeField] private string learnedFileName = "learned_tags.json";

    [Header("Scan rate (seconds between frames)")]
    [SerializeField] private float fastScanInterval = 0f;
    [SerializeField] private float scanInterval = 0.5f;
    [SerializeField] private float anchoredScanInterval = 3f;

    [Header("Observations")]
    [Tooltip("Most recent observations kept per tag for placing the room.")]
    [SerializeField] private int historyPerTag = 30;
    [Tooltip("Observations older than this no longer place the room, so tracking drift ages out.")]
    [SerializeField] private float observationMaxAgeSeconds = 60f;
    [Tooltip("An observation this far (m) from where its tag is expected disagrees.")]
    [SerializeField] private float outlierDistance = 0.10f;
    [Tooltip("This many disagreeing observations in a row of a measured or established tag mean the tracking frame moved.")]
    [SerializeField] private int outliersForReset = 3;

    [Tooltip("Seconds for the displayed pose to ease toward a new estimate (0 = jump).")]
    [SerializeField] private float smoothingSeconds = 0.3f;

    [Tooltip("Config drawn as Scene-view gizmos (read from StreamingAssets in the editor only).")]
    [SerializeField] private string gizmoConfigFileName = "room_config.json";

    // Raised after every pose update.
    public event Action<RoomAnchor> Localized;

    public bool IsLocalized { get; private set; }
    // Placed from measured tags but not yet good enough to learn from.
    public bool IsProvisional => IsLocalized && !IsAnchored;
    public bool IsAnchored { get; private set; }
    public RoomFitResult? LastFit { get; private set; }

    private sealed class TagHistory
    {
        public readonly List<TagObservation> Observations = new();
        public int Outliers;
        public Vector3 MeanWorldPosition;

        public void Recompute()
        {
            var sum = Vector3.zero;
            foreach (var o in Observations)
            {
                sum += o.WorldPosition;
            }
            MeanWorldPosition = Observations.Count > 0 ? sum / Observations.Count : Vector3.zero;
        }
    }

    [Serializable]
    private sealed class LearnedTag
    {
        public int id;
        public float x, y, z;
        public float weightSum;
        public int samples;
        // Weighted sum of the tag's facing direction in room axes (x, z).
        public float yawX, yawZ;
        [NonSerialized] public int disagreements;
        // Loaded from a saved survey: its position stays fixed, so devices
        // sharing the file keep identical tag positions.
        [NonSerialized] public bool frozen;
        [NonSerialized] public float rotationSpeedSum;
        [NonSerialized] public float rotationSpeedMax;

        public Vector3 Position => new(x, y, z);
        public float YawDegrees => Mathf.Repeat(Mathf.Atan2(yawX, yawZ) * Mathf.Rad2Deg, 360f);
    }

    [Serializable]
    private sealed class LearnedFile
    {
        public string measuredTagsFingerprint;
        public List<LearnedTag> tags = new();
    }

    private readonly SortedDictionary<int, TagHistory> histories = new();
    private readonly SortedDictionary<int, LearnedTag> learned = new();
    private bool learnedLoaded;
    private bool learnedDirty;
    private float lastSaveTime;
    private Vector3 targetPosition;
    private Quaternion targetRotation = Quaternion.identity;
    private bool snapNext = true;
    private string lastLoggedState = "";
    private int lastLoggedCloseSightings = -1;
    private float lastDistanceLogTime = float.NegativeInfinity;
    private int surveyAnchorId = -1;
    private bool childrenShown;
    private readonly HashSet<int> loggedNotUpright = new();

    public bool SurveyMode => surveyMode;
    // In survey mode, the tag that defines the room (-1 until one is seen).
    public int SurveyAnchorId => surveyAnchorId;

    // Scans at full rate until a tag locks; its frames count like any others.
    public void Rescan()
    {
        if (localizer != null && !localizer.IsAcquiring)
        {
            localizer.BeginAcquisition();
        }
    }

    // Forgets this session's observations and re-anchors; learned positions stay.
    public void ClearTags()
    {
        histories.Clear();
        LastFit = null;
        IsAnchored = false;
        snapNext = true;
        lastLoggedCloseSightings = -1;
        if (surveyMode)
        {
            surveyAnchorId = -1;
        }
        UpdateChildrenVisibility();
        Debug.Log("[RoomAnchor] Observations cleared; re-anchoring");
    }

    // Forgets every learned tag position, including the saved file.
    public void ForgetLearnedTags()
    {
        learned.Clear();
        learnedDirty = true;
        SaveLearned();
        Debug.Log("[RoomAnchor] Learned tag positions forgotten");
    }

    // Where a tag is in room coordinates: surveyed for measured tags, learned for
    // others once established. For drawing markers or placing content by tag.
    public bool TryGetTagRoomPosition(int tagId, out Vector3 roomPosition, out bool measured)
    {
        roomPosition = default;
        measured = false;
        if (TryGetAnchorPose(tagId, out var anchorPosition, out _))
        {
            roomPosition = anchorPosition;
            measured = true;
            return true;
        }
        if (learned.TryGetValue(tagId, out var tag) && tag.samples >= establishedSamples)
        {
            roomPosition = tag.Position;
            return true;
        }
        return false;
    }

    // Tags with a room position (measured, or learned and established).
    public IEnumerable<int> PlacedTagIds =>
        histories.Keys.Concat(learned.Keys).Distinct().Where(id => TryGetTagRoomPosition(id, out _, out _));

    public string StatusText
    {
        get
        {
            var text = new StringBuilder();
            if (!IsLocalized)
            {
                text.Append(!surveyMode ? "Room: looking for a measured tag"
                    : localizer != null && localizer.ListedTagCount > 0 ? "Survey: look at the tag listed in room_config.json"
                    : "Survey: look at any tag - it will define the room origin");
            }
            else if (!IsAnchored)
            {
                // Progress toward anchoring on the closest measured tag in view.
                var closest = histories.Where(h => h.Value.Observations.Count > 0 && IsMeasured(h.Key))
                    .OrderBy(h => h.Value.Observations[^1].Distance).FirstOrDefault();
                var close = closest.Value?.Observations.Count(o => o.Distance <= anchorMaxDistance) ?? 0;
                text.Append(SingleTagAnchoringAllowed
                    ? $"Room: provisional - get within {anchorMaxDistance:F1} m of tag {closest.Key} ({Mathf.Min(close, anchorSamples)}/{anchorSamples} close sightings)"
                    : "Room: provisional - get a second tag in view");
            }
            else
            {
                text.Append(LastFit.HasValue
                    ? $"Room: anchored, fit from {LastFit.Value.Residuals.Count} tags, RMS {LastFit.Value.RmsResidual * 100f:F1} cm"
                    : "Room: anchored");
            }
            if (localizer != null)
            {
                text.Append($"   scan every {localizer.ScanIntervalSeconds:F1}s");
            }
            text.AppendLine();
            foreach (var id in histories.Keys.Concat(learned.Keys).Distinct().OrderBy(i => i))
            {
                histories.TryGetValue(id, out var history);
                learned.TryGetValue(id, out var tag);
                text.Append($"  tag {id}: ");
                if (IsMeasured(id))
                {
                    text.Append(surveyMode ? "anchor" : "measured");
                }
                else if (tag != null && tag.samples >= establishedSamples)
                {
                    text.Append($"learned ({tag.samples})");
                }
                else if (tag != null)
                {
                    text.Append($"learning ({tag.samples}/{establishedSamples})");
                }
                else
                {
                    text.Append(IsAnchored ? "seen" : "seen - learned once the room is anchored");
                }
                if (history != null && history.Observations.Count > 0)
                {
                    var last = history.Observations[^1];
                    var age = Time.time - last.Time;
                    text.Append(age < 1f ? $", {last.Distance:F1} m away" : $", last seen {age:F0}s ago");
                }
                text.AppendLine();
            }
            return text.ToString();
        }
    }

    // Anchor tags place the room: the config's measured tags, or in survey mode
    // the survey's anchor.
    private bool IsMeasured(int tagId) => surveyMode
        ? tagId == surveyAnchorId
        : localizer != null && localizer.TryGetConfiguredTag(tagId, out _, out _, out var measured) && measured;

    // An anchor tag's pose in room coordinates: from the config, or for a survey
    // of a room the config lists no tags for, the origin.
    private bool TryGetAnchorPose(int tagId, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (!IsMeasured(tagId))
        {
            return false;
        }
        localizer.TryGetConfiguredTag(tagId, out position, out rotation, out _);
        return true;
    }

    // The room origin's (yaw-only) rotation implied by one sighting of an anchor
    // tag: the room is the tag's world pose undone by its room pose.
    private static Quaternion OriginRotationFrom(TagObservation o, Quaternion tagRoomRotation)
    {
        var relative = o.WorldRotation * Quaternion.Inverse(tagRoomRotation);
        return Quaternion.LookRotation(Vector3.ProjectOnPlane(relative * Vector3.forward, Vector3.up), Vector3.up);
    }

    // One tag may anchor only in a survey, or when the config has a single
    // measured tag; otherwise two must be fitted.
    private bool SingleTagAnchoringAllowed =>
        surveyMode || !requireTwoTagsWhenAvailable || localizer == null || localizer.MeasuredTagCount < 2;

    private bool IsEstablished(int tagId) => learned.TryGetValue(tagId, out var tag) && tag.samples >= establishedSamples;

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
            localizer.ConfigChanged += OnConfigChanged;
        }
    }

    // A new room (e.g. from a scanned room code): start over - observations,
    // anchoring, and learned tags (reloaded for the new survey).
    private void OnConfigChanged(RoomConfig config)
    {
        histories.Clear();
        learned.Clear();
        learnedLoaded = false;
        LastFit = null;
        IsAnchored = false;
        IsLocalized = false;
        surveyAnchorId = -1;
        snapNext = true;
        lastLoggedCloseSightings = -1;
        lastLoggedState = "";
        UpdateChildrenVisibility();
    }

    private void OnDisable()
    {
        if (localizer != null)
        {
            localizer.TagObserved -= OnTagObserved;
            localizer.ConfigChanged -= OnConfigChanged;
        }
        SaveLearned();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            SaveLearned();
        }
    }

    private void Start()
    {
        localizer.ContinuousScan = scanContinuously;
    }

    private void Update()
    {
        if (!learnedLoaded && localizer.IsConfigLoaded)
        {
            if (surveyMode)
            {
                // A survey learns every tag it sees, listed or not.
                var size = localizer.DefaultTagSizeMeters;
                if (size <= 0f && localizer.LoadedConfig.tags.Length > 0)
                {
                    size = localizer.LoadedConfig.tags[0].sizeMeters;
                }
                localizer.EnableUnlistedTags(size);
                Debug.Log(size > 0f
                    ? $"[RoomAnchor] Survey: learning every tag at {size * 100f:F2} cm; anchor = the first {(localizer.ListedTagCount > 0 ? "tag listed in the config" : "tag seen (it defines the room origin)")}"
                    : "[RoomAnchor] Survey: the config gives no tag size (defaultTagSizeMeters) - unlisted tags can't be measured");
            }
            LoadLearned();
        }
        if (learnedDirty && Time.time - lastSaveTime > 5f)
        {
            SaveLearned();
        }

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
        if (!learnedLoaded)
        {
            return;
        }

        var id = observation.TagId;
        if (surveyMode && surveyAnchorId < 0 && (observation.Listed || localizer.ListedTagCount == 0))
        {
            surveyAnchorId = id;
            Debug.Log($"[RoomAnchor] Survey anchor: tag {id} ({(observation.Listed ? "pose from the config" : "defines the room origin")})");
        }
        var isMeasured = IsMeasured(id);
        var trusted = isMeasured || IsEstablished(id);

        if (!histories.TryGetValue(id, out var history))
        {
            history = new TagHistory();
            histories[id] = history;
        }

        // Does a trusted tag disagree with where the room says it is? Repeatedly:
        // the tracking frame moved. Untrusted tags are checked in Learn instead.
        if (trusted && IsAnchored && TryGetTagRoomPosition(id, out var roomPosition, out _))
        {
            var expected = targetPosition + targetRotation * roomPosition;
            if (Vector3.Distance(observation.WorldPosition, expected) > outlierDistance)
            {
                if (++history.Outliers < outliersForReset)
                {
                    return;
                }
                Debug.Log($"[RoomAnchor] Tag {id} seen {Vector3.Distance(observation.WorldPosition, expected) * 100f:F0} cm from where the room puts it, {history.Outliers} times in a row; tracking frame moved - re-anchoring");
                ClearTags();
                history = new TagHistory();
                histories[id] = history;
            }
            else
            {
                history.Outliers = 0;
            }
        }

        history.Observations.Add(observation);
        if (history.Observations.Count > historyPerTag)
        {
            history.Observations.RemoveRange(0, history.Observations.Count - historyPerTag);
        }

        Refit();

        if (IsAnchored && !isMeasured)
        {
            Learn(observation);
        }
    }

    private void Refit()
    {
        var cutoff = Time.time - observationMaxAgeSeconds;
        foreach (var h in histories.Values)
        {
            h.Observations.RemoveAll(o => o.Time < cutoff);
            h.Recompute();
        }

        // Tags with a room position, seen recently: measured at full weight,
        // established learned tags (only once anchored) at less.
        var correspondences = new List<TagCorrespondence>();
        foreach (var (id, h) in histories)
        {
            if (h.Observations.Count == 0)
            {
                continue;
            }
            if (TryGetAnchorPose(id, out var roomPosition, out _))
            {
                correspondences.Add(new TagCorrespondence(id, roomPosition, h.MeanWorldPosition));
            }
            else if (IsEstablished(id))
            {
                correspondences.Add(new TagCorrespondence(id, learned[id].Position, h.MeanWorldPosition, learnedTagWeight));
            }
        }
        var measuredCount = correspondences.Count(c => IsMeasured(c.TagId));

        if (correspondences.Count >= 2 && RoomFit.TryFit(correspondences, out var fit))
        {
            // Any two or more tags with known room positions - measured, or
            // learned (this session, or a saved survey shared between devices) -
            // anchor by fitting positions: no single tag's yaw is used, which is
            // only reliable to ~1-2 deg (iPhone) even up close.
            LastFit = fit;
            SetTarget(fit.Position, fit.Rotation);
            if (!IsAnchored)
            {
                IsAnchored = true;
                Debug.Log($"[RoomAnchor] Anchored by fitting {correspondences.Count} tags' positions ({measuredCount} measured, {correspondences.Count - measuredCount} learned)");
            }
        }
        else if (IsAnchored)
        {
            // One tag in view: correct position drift from it, keep the yaw.
            LastFit = null;
            var best = correspondences.OrderByDescending(c => c.Weight).FirstOrDefault();
            if (correspondences.Count > 0)
            {
                SetTarget(best.WorldPosition - targetRotation * best.RoomPosition, targetRotation);
            }
        }
        else
        {
            // Not anchored: place from the measured tag seen closest, yaw from its
            // observations weighted toward close range; anchored once enough of
            // them are close.
            var anchorEntry = histories.Where(h => h.Value.Observations.Count > 0 && IsMeasured(h.Key))
                .OrderBy(h => h.Value.Observations.Average(o => o.Distance)).FirstOrDefault();
            var measured = anchorEntry.Value;
            if (measured == null || !TryGetAnchorPose(anchorEntry.Key, out var tagRoomPosition, out var tagRoomRotation))
            {
                return;
            }
            var close = measured.Observations.Count(o => o.Distance <= anchorMaxDistance);
            var forward = Vector3.zero;
            foreach (var o in measured.Observations)
            {
                // Once anchoring, yaw from the close sightings only.
                if (close < anchorSamples || o.Distance <= anchorMaxDistance)
                {
                    forward += OriginRotationFrom(o, tagRoomRotation) * Vector3.forward / Mathf.Max(o.Distance * o.Distance, 0.01f);
                }
            }
            var rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, Vector3.up), Vector3.up);
            LastFit = null;
            SetTarget(measured.MeanWorldPosition - rotation * tagRoomPosition, rotation);
            LogAnchoringProgress(measured.Observations[^1], close);
            if (close >= anchorSamples && SingleTagAnchoringAllowed)
            {
                IsAnchored = true;
                LogAnchoringSpread(measured.Observations.Where(o => o.Distance <= anchorMaxDistance).ToList(), rotation, tagRoomRotation);
            }
        }

        LogStateChange(correspondences.Count);
        UpdateChildrenVisibility();
        Localized?.Invoke(this);
    }

    private void SetTarget(Vector3 position, Quaternion rotation)
    {
        targetPosition = position;
        targetRotation = rotation;
        if (!IsLocalized || snapNext)
        {
            transform.SetPositionAndRotation(position, rotation);
            snapNext = false;
        }
        IsLocalized = true;
    }

    // Content appears once anchored and hides again while re-anchoring.
    private void UpdateChildrenVisibility()
    {
        if (hideUntilLocalized && childrenShown != IsAnchored)
        {
            childrenShown = IsAnchored;
            SetChildrenActive(IsAnchored);
        }
    }

    // Folds one sighting of an unmeasured tag into its learned room position,
    // weighted toward close range (position error grows with distance).
    private void Learn(TagObservation observation)
    {
        // Wall-mounted tags only: a tag lying on a table faces up.
        var facing = Quaternion.Inverse(targetRotation) * observation.WorldRotation * Vector3.forward;
        if (Mathf.Abs(facing.y) > Mathf.Sin(maxTagTiltDegrees * Mathf.Deg2Rad))
        {
            if (loggedNotUpright.Add(observation.TagId))
            {
                Debug.Log($"[RoomAnchor] Ignoring tag {observation.TagId}: not mounted upright (faces {Mathf.Asin(Mathf.Clamp(facing.y, -1f, 1f)) * Mathf.Rad2Deg:F0} deg from horizontal)");
            }
            return;
        }
        var roomPosition = Quaternion.Inverse(targetRotation) * (observation.WorldPosition - targetPosition);
        var weight = 1f / Mathf.Max(observation.Distance * observation.Distance, 0.25f);
        if (!learned.TryGetValue(observation.TagId, out var tag))
        {
            tag = new LearnedTag { id = observation.TagId };
            learned[observation.TagId] = tag;
            Debug.Log($"[RoomAnchor] Learning tag {observation.TagId}{(observation.Listed ? "" : " (unlisted)")}");
        }
        else if (tag.samples >= 5 && Vector3.Distance(roomPosition, tag.Position) > outlierDistance)
        {
            if (++tag.disagreements >= disagreementsToUnlearn)
            {
                Debug.Log($"[RoomAnchor] Tag {tag.id} keeps appearing {Vector3.Distance(roomPosition, tag.Position) * 100f:F0} cm from its learned position (moved, or not fixed?); relearning it");
                learned.Remove(tag.id);
                learnedDirty = true;
            }
            return;
        }

        tag.disagreements = 0;
        if (tag.frozen)
        {
            return;
        }
        var position = (tag.Position * tag.weightSum + roomPosition * weight) / (tag.weightSum + weight);
        (tag.x, tag.y, tag.z) = (position.x, position.y, position.z);
        tag.weightSum += weight;
        tag.samples++;
        var flat = new Vector2(facing.x, facing.z).normalized * weight;
        tag.yawX += flat.x;
        tag.yawZ += flat.y;
        tag.rotationSpeedSum += observation.RotationSpeed;
        tag.rotationSpeedMax = Mathf.Max(tag.rotationSpeedMax, observation.RotationSpeed);
        learnedDirty = true;
        if (tag.samples == establishedSamples)
        {
            Debug.Log($"[RoomAnchor] Learned tag {tag.id} at room ({position.x:F3}, {position.y:F3}, {position.z:F3}), yaw {tag.YawDegrees:F1}; turning {tag.rotationSpeedSum / tag.samples:F1} deg/s mean, {tag.rotationSpeedMax:F1} max over those sightings");
        }
    }

    // The room as surveyed: the anchor tag plus every established learned tag,
    // all marked measured, with the loaded config's room size and settings.
    public RoomConfig BuildSurveyedConfig()
    {
        var loaded = localizer.LoadedConfig;
        var tags = new List<TagPlacement>();
        if (surveyAnchorId >= 0 && TryGetAnchorPose(surveyAnchorId, out var anchorPosition, out var anchorRotation))
        {
            tags.Add(new TagPlacement
            {
                id = surveyAnchorId, x = anchorPosition.x, y = anchorPosition.y, z = anchorPosition.z,
                yawDegrees = Mathf.Repeat(anchorRotation.eulerAngles.y, 360f), sizeMeters = SizeOf(surveyAnchorId), measured = true,
            });
        }
        foreach (var tag in learned.Values.Where(t => t.samples >= establishedSamples && t.id != surveyAnchorId))
        {
            tags.Add(new TagPlacement
            {
                id = tag.id, x = tag.x, y = tag.y, z = tag.z,
                yawDegrees = tag.YawDegrees, sizeMeters = SizeOf(tag.id), measured = true,
            });
        }
        return new RoomConfig
        {
            name = loaded?.name,
            dataManager = loaded?.dataManager,
            room = loaded?.room,
            learnUnlistedTags = loaded?.learnUnlistedTags ?? true,
            defaultTagSizeMeters = localizer.DefaultTagSizeMeters,
            tags = tags.ToArray(),
        };
    }

    private float SizeOf(int tagId) =>
        localizer.TryGetConfiguredTagSize(tagId, out var size) ? size : localizer.DefaultTagSizeMeters;

    // Writes the surveyed room to persistentDataPath under the config's name, where
    // it overrides the bundled config on this device from the next start (the
    // previous override is kept as .prev.json). Returns the path and tag count.
    public (string path, int tags) SaveSurveyedConfig()
    {
        var config = BuildSurveyedConfig();
        var path = Path.Combine(Application.persistentDataPath, localizer.ConfigFileName);
        if (File.Exists(path))
        {
            File.Copy(path, Path.ChangeExtension(path, ".prev.json"), true);
        }
        var json = JsonUtility.ToJson(config, true);
        File.WriteAllText(path, json);
        Debug.Log($"[RoomAnchor] Survey saved: {config.tags.Length} tags to {path}\n{json}");
        return (path, config.tags.Length);
    }

    private string LearnedPath => Path.Combine(Application.persistentDataPath, learnedFileName);

    private void LoadLearned()
    {
        learnedLoaded = true;
        if (!File.Exists(LearnedPath))
        {
            return;
        }
        try
        {
            var file = JsonUtility.FromJson<LearnedFile>(File.ReadAllText(LearnedPath));
            if (file.measuredTagsFingerprint != localizer.MeasuredTagsFingerprint)
            {
                Debug.Log("[RoomAnchor] Saved learned tags were learned against different measured tags; ignoring them");
                return;
            }
            foreach (var tag in file.tags)
            {
                tag.frozen = !surveyMode && tag.samples >= establishedSamples;
                learned[tag.id] = tag;
            }
            Debug.Log($"[RoomAnchor] Loaded {learned.Count} learned tag(s) from {LearnedPath}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RoomAnchor] Couldn't read {LearnedPath}: {e.Message}");
        }
    }

    private void SaveLearned()
    {
        if (!learnedDirty || !learnedLoaded || localizer == null)
        {
            return;
        }
        learnedDirty = false;
        lastSaveTime = Time.time;
        try
        {
            var file = new LearnedFile { measuredTagsFingerprint = localizer.MeasuredTagsFingerprint, tags = learned.Values.ToList() };
            File.WriteAllText(LearnedPath, JsonUtility.ToJson(file, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RoomAnchor] Couldn't save {LearnedPath}: {e.Message}");
        }
    }

    // While provisional: each new close sighting, and every few seconds the
    // distance if the measured tag is only seen from too far to anchor.
    private void LogAnchoringProgress(TagObservation latest, int closeSightings)
    {
        if (closeSightings != lastLoggedCloseSightings && closeSightings > 0)
        {
            lastLoggedCloseSightings = closeSightings;
            Debug.Log($"[RoomAnchor] Tag {latest.TagId} at {latest.Distance:F2} m, turning {latest.RotationSpeed:F1} deg/s: {Mathf.Min(closeSightings, anchorSamples)}/{anchorSamples} close sightings (within {anchorMaxDistance:F1} m) to anchor");
        }
        else if (latest.Distance > anchorMaxDistance && Time.time - lastDistanceLogTime > 3f)
        {
            lastDistanceLogTime = Time.time;
            Debug.Log($"[RoomAnchor] Tag {latest.TagId} seen at {latest.Distance:F2} m; within {anchorMaxDistance:F1} m needed to anchor ({closeSightings}/{anchorSamples} close sightings so far)");
        }
    }

    // How much the close frames that anchored the room disagree on its yaw, and
    // where the camera was relative to the tag (degrees off square horizontally,
    // above/below), to tell per-frame noise from viewpoint-dependent bias.
    private void LogAnchoringSpread(List<TagObservation> close, Quaternion anchoredRotation, Quaternion tagRotation)
    {
        var meanYaw = anchoredRotation.eulerAngles.y;
        float sq = 0f, min = float.MaxValue, max = float.MinValue;
        Vector3 view = Vector3.zero;
        foreach (var o in close)
        {
            var d = Mathf.DeltaAngle(meanYaw, OriginRotationFrom(o, tagRotation).eulerAngles.y);
            sq += d * d;
            min = Mathf.Min(min, d);
            max = Mathf.Max(max, d);
            view += o.CameraPosition - o.WorldPosition;
        }
        view /= close.Count;

        // Camera direction from the tag, in room axes, against the direction a
        // viewer faces the tag from (opposite its facing).
        var id = close[0].TagId;
        var viewRoom = Quaternion.Inverse(anchoredRotation) * view;
        var square = -(tagRotation * Vector3.forward);
        var horizontal = Vector3.SignedAngle(Vector3.ProjectOnPlane(square, Vector3.up), Vector3.ProjectOnPlane(viewRoom, Vector3.up), Vector3.up);
        var vertical = Mathf.Asin(Mathf.Clamp(viewRoom.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        Debug.Log($"[RoomAnchor] Anchor yaw from tag {id}'s {close.Count} close frames: {meanYaw:F2} deg, per-frame sd {Mathf.Sqrt(sq / close.Count):F2}, range {min:+0.00;-0.00} to {max:+0.00;-0.00} deg; " +
                  $"camera {view.magnitude:F2} m away, {horizontal:+0.0;-0.0} deg off square, {vertical:+0.0;-0.0} deg above the tag");
    }

    private void LogStateChange(int fittedTags)
    {
        var state = !IsAnchored ? "provisional" : LastFit.HasValue ? $"fit {fittedTags}" : "anchored";
        if (state == lastLoggedState)
        {
            return;
        }
        lastLoggedState = state;
        if (LastFit.HasValue)
        {
            var residuals = string.Join(", ", LastFit.Value.Residuals
                .OrderBy(r => r.tagId)
                .Select(r => $"tag {r.tagId}{(IsMeasured(r.tagId) ? "" : " (learned)")} {r.residual.magnitude * 100f:F1}"));
            Debug.Log($"[RoomAnchor] Anchored, fit from {fittedTags} tags: RMS residual {LastFit.Value.RmsResidual * 100f:F1} cm ({residuals} cm); origin {targetPosition} yaw {targetRotation.eulerAngles.y:F1}");
        }
        else
        {
            Debug.Log($"[RoomAnchor] {(IsAnchored ? "Anchored by one measured tag up close" : "Provisional placement")}; origin {targetPosition} yaw {targetRotation.eulerAngles.y:F1}");
        }
    }

    // Fast while a recently seen tag still needs samples (to anchor or learn);
    // slow once anchored.
    private void UpdateScanRate()
    {
        if (localizer == null)
        {
            return;
        }
        var recent = Time.time - 2f;
        var needsSamples = histories.Any(h =>
            h.Value.Observations.Count > 0 && h.Value.Observations[^1].Time >= recent &&
            (IsMeasured(h.Key) ? !IsAnchored : IsAnchored && !IsEstablished(h.Key)));
        localizer.ScanIntervalSeconds = needsSamples ? fastScanInterval : IsAnchored ? anchoredScanInterval : scanInterval;
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
