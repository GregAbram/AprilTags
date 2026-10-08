using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AprilTag.Interop;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

// The room origin's pose in this session's world space, as solved from one tag.
public readonly struct RoomOriginEstimate
{
    public readonly int TagId;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly int SampleCount;
    public readonly Vector3 TagPositionCameraLocal;

    // The tag's center as detected in this session's world space (averaged over
    // the window) and as configured in room coordinates - the pair RoomFit uses.
    public readonly Vector3 TagWorldPosition;
    public readonly Vector3 TagRoomPosition;

    public RoomOriginEstimate(int tagId, Vector3 position, Quaternion rotation, int sampleCount,
        Vector3 tagPositionCameraLocal, Vector3 tagWorldPosition, Vector3 tagRoomPosition)
    {
        TagId = tagId;
        Position = position;
        Rotation = rotation;
        SampleCount = sampleCount;
        TagPositionCameraLocal = tagPositionCameraLocal;
        TagWorldPosition = tagWorldPosition;
        TagRoomPosition = tagRoomPosition;
    }

    // World position of a point given in room coordinates.
    public Vector3 RoomToWorld(Vector3 roomPosition) => Position + Rotation * roomPosition;
}

// One configured tag seen in one camera frame, in this session's world space.
// OriginPosition/OriginRotation are the room origin as solved from this tag
// alone (yaw-only); its yaw is noisy beyond ~1 m, so prefer fitting several
// tags' WorldPositions (RoomFit) when more than one has been seen.
public readonly struct TagObservation
{
    public readonly int TagId;
    // Measured: listed with a surveyed pose that defines the room frame.
    // Listed: in the config at all (an unlisted tag was detected because the
    // config allows learning unlisted tags; its TagRoomPosition is meaningless).
    public readonly bool Measured;
    public readonly bool Listed;
    public readonly float Time;
    public readonly float Distance;
    // How fast the camera was turning when the frame was taken (deg/s).
    public readonly float RotationSpeed;
    // Where the camera was when the frame was taken, in this session's world space.
    public readonly Vector3 CameraPosition;
    public readonly Vector3 TagRoomPosition;
    public readonly Vector3 WorldPosition;
    public readonly Vector3 OriginPosition;
    public readonly Quaternion OriginRotation;

    public TagObservation(int tagId, bool measured, bool listed, float time, float distance, float rotationSpeed,
        Vector3 cameraPosition, Vector3 tagRoomPosition, Vector3 worldPosition, Vector3 originPosition, Quaternion originRotation)
    {
        CameraPosition = cameraPosition;
        TagId = tagId;
        Measured = measured;
        Listed = listed;
        Time = time;
        Distance = distance;
        RotationSpeed = rotationSpeed;
        TagRoomPosition = tagRoomPosition;
        WorldPosition = worldPosition;
        OriginPosition = originPosition;
        OriginRotation = originRotation;
    }
}

// Detects the tags listed in the room config and solves for the room origin's
// pose in this session. Detection runs on a worker thread; the main thread only
// requests frames and applies results. Two ways to use it:
//   - Acquisition: each BeginAcquisition() produces one estimate - from
//     whichever known tag first stays in view for a full acquisition window -
//     and raises EstimateAcquired.
//   - Continuous scan (ContinuousScan): a frame every ScanIntervalSeconds, and
//     every known tag in it is reported as a single-frame TagObserved; RoomAnchor
//     builds the room from these.
// TagObserved is raised for every processed frame in either mode. Frames come
// from an AprilTagCameraSource: the assigned one, else one on this GameObject,
// else the first in the scene.
public class AprilTagRoomLocalizer : MonoBehaviour
{
    [SerializeField] private AprilTagCameraSource cameraSource;
    [SerializeField] private int quadDecimate = 2;
    [SerializeField] private string configFileName = "room_config.json";
    [SerializeField] private float trackingSettleDelaySeconds = 2f;
    [SerializeField] private float acquisitionWindowSeconds = 1f;
    [SerializeField] private float maxSampleGapSeconds = 0.25f;
    [SerializeField] private float maxOffAxisAngleDegrees = 20f;

    [Tooltip("Scan for tags continuously at ScanIntervalSeconds, reporting each sighting via TagObserved.")]
    [SerializeField] private bool continuousScan;
    [Tooltip("Seconds between continuous-scan frames (0 = every camera frame).")]
    [SerializeField] private float scanIntervalSeconds = 0.5f;

    [Tooltip("Frames taken while the camera turns faster than this (deg/s) are dropped: a small mismatch between image and pose times becomes a rotation error. 0 = keep all.")]
    [SerializeField] private float maxRotationSpeedDegreesPerSecond = 8f;

    [Tooltip("Log per-frame timing (image prepare, detection, pose, apply, latency) this often while scanning or acquiring; also when an acquisition ends.")]
    [SerializeField] private float timingLogIntervalSeconds = 30f;

    public event Action<RoomOriginEstimate> EstimateAcquired;
    public event Action<TagObservation> TagObserved;

    // True from BeginAcquisition() until an estimate is raised or cancelled,
    // including while still starting up (config load, camera, settle delay).
    public bool IsAcquiring => armed;
    public bool IsReady => detector != null;
    public IReadOnlyCollection<int> KnownTagIds => tagRegistry.Keys;

    public bool ContinuousScan
    {
        get => continuousScan;
        set => continuousScan = value;
    }

    public float ScanIntervalSeconds
    {
        get => scanIntervalSeconds;
        set => scanIntervalSeconds = Mathf.Max(0f, value);
    }

    private bool armed;
    private float armedTime;
    private bool acquiring;
    private int acquiringTagId;
    private float lastSampleTime;
    private float firstSampleTime;
    private Vector3 positionSum;
    private Vector3 forwardSum;
    private Vector2 pixelCenterSum;
    private Vector3 cameraLocalPositionSum;
    private Vector3 tagWorldPositionSum;
    private int sampleCount;

    // Per-sample values in the current window, for the per-frame spread logged
    // on lock (how good a single frame is compared with the window's average).
    private readonly List<Vector3> sampleTagPositions = new();
    private readonly List<Vector3> sampleOriginPositions = new();
    private readonly List<float> sampleYaws = new();

    private Detector detector;
    private Family family;
    private Action<bool, CameraFrame> onFrameCaptured;
    private float principalPointCx;
    private float principalPointCy;
    private float focalLengthFx;
    private float focalLengthFy;
    private PinholeIntrinsics startupIntrinsics;
    private PinholeIntrinsics pendingIntrinsics;
    private float lastLoggedFx;
    private readonly Dictionary<int, (Vector3 position, Quaternion rotation, float sizeMeters, bool measured)> tagRegistry = new();
    private bool learnUnlistedTags;
    private float defaultTagSizeMeters;

    public bool IsConfigLoaded { get; private set; }

    // Identifies the measured tags' surveyed poses, so learned positions saved
    // under a different survey can be recognized and discarded.
    public string MeasuredTagsFingerprint { get; private set; } = "";

    // A listed tag's configured room pose (meaningful as ground truth only if measured).
    public bool TryGetConfiguredTag(int tagId, out Vector3 roomPosition, out Quaternion roomRotation, out bool measured)
    {
        var found = tagRegistry.TryGetValue(tagId, out var tag);
        roomPosition = tag.position;
        roomRotation = tag.rotation;
        measured = found && tag.measured;
        return found;
    }

    // One frame in flight at a time: requested, then detected on a worker, then
    // applied here. The source doesn't rewrite its image until the next request.
    private bool frameInFlight;
    private long frameRequestTimestamp;
    private float frameRequestTime;
    private float lastScanRequestTime = float.NegativeInfinity;
    private Task<DetectionResult> pendingDetection;
    private CameraFrame pendingFrame;
    private double pendingPrepareMs;

    private readonly FrameTiming timing = new();

    // Camera rotation speed, from the main camera's motion each Update.
    private Transform rotationReference;
    private Quaternion lastRotation;
    private float lastRotationTime = -1f;
    private float rotationSpeed;
    private float frameRotationSpeed;
    private float lastTimingLogTime;

    // For components that add the localizer at runtime; call before its Start().
    public void Configure(AprilTagCameraSource camera, int decimate, string configFile,
        float settleDelaySeconds, float windowSeconds, float sampleGapSeconds, float offAxisAngleDegrees)
    {
        cameraSource = camera;
        quadDecimate = decimate;
        configFileName = configFile;
        trackingSettleDelaySeconds = settleDelaySeconds;
        acquisitionWindowSeconds = windowSeconds;
        maxSampleGapSeconds = sampleGapSeconds;
        maxOffAxisAngleDegrees = offAxisAngleDegrees;
    }

    // Starts (or restarts) looking for a tag. Safe to call before the localizer
    // is ready; acquisition begins as soon as it is.
    public void BeginAcquisition()
    {
        armed = true;
        armedTime = Time.time;
        acquiring = false;
    }

    public void CancelAcquisition()
    {
        if (armed)
        {
            LogTiming("cancelled");
        }
        armed = false;
        acquiring = false;
    }

    private System.Collections.IEnumerator Start()
    {
        if (cameraSource == null)
        {
            cameraSource = GetComponent<AprilTagCameraSource>();
        }
        if (cameraSource == null)
        {
            cameraSource = FindAnyObjectByType<AprilTagCameraSource>();
        }
        if (cameraSource == null)
        {
            Debug.LogError("[AprilTagRoomLocalizer] No AprilTagCameraSource assigned or found in the scene (on Quest, add a PassthroughCameraSource) - nothing will be detected");
            yield break;
        }

        yield return LoadRoomConfig();

        while (!cameraSource.IsPlaying || !cameraSource.TryGetIntrinsics(out _))
        {
            yield return null;
        }

        // Tracking (visual+IMU fusion) may not be fully settled immediately after
        // the session starts; a pose queried too early can differ measurably from
        // one queried a moment later even with no physical movement. Wait before
        // allowing a lock so early, less-settled poses aren't used.
        yield return new WaitForSeconds(trackingSettleDelaySeconds);

        PinholeIntrinsics intrinsics;
        while (!cameraSource.TryGetIntrinsics(out intrinsics))
        {
            yield return null;
        }

        detector = Detector.Create();
        detector.QuadDecimate = quadDecimate;
        family = Family.CreateTagStandard41h12();
        detector.AddFamily(family);
        onFrameCaptured = OnFrameCaptured;

        // An earlier empirical focal scale factor (0.5543) made centered depth
        // measurements match on Quest, but ground-truth testing at a known
        // off-axis angle showed it produces a large lateral/angular error - the
        // remaining depth error was tag size, not focal length (see sizeMeters in
        // room_config.json). Use the source's intrinsics as reported.
        focalLengthFx = intrinsics.Fx;
        focalLengthFy = intrinsics.Fy;
        principalPointCx = intrinsics.Cx;
        principalPointCy = intrinsics.Cy;
        startupIntrinsics = intrinsics;
        lastLoggedFx = intrinsics.Fx;

        Debug.Log($"[AprilTagRoomLocalizer] Camera intrinsics {intrinsics}");
        timing.Reset(Time.realtimeSinceStartup);
        lastTimingLogTime = Time.time;
    }

    private System.Collections.IEnumerator LoadRoomConfig()
    {
        var overridePath = Path.Combine(Application.persistentDataPath, configFileName);
        string json = null;

        if (File.Exists(overridePath))
        {
            json = File.ReadAllText(overridePath);
        }
        else if (!Application.streamingAssetsPath.Contains("://"))
        {
            // iOS and desktop: StreamingAssets is a plain directory, which
            // UnityWebRequest can't load from on iOS.
            var streamingPath = Path.Combine(Application.streamingAssetsPath, configFileName);
            if (File.Exists(streamingPath))
            {
                json = File.ReadAllText(streamingPath);
            }
            else
            {
                Debug.LogError($"[AprilTagRoomLocalizer] No room config at {streamingPath} or {overridePath}");
            }
        }
        else
        {
            // Android: StreamingAssets is inside the APK, reachable only as a URL.
            var streamingPath = Path.Combine(Application.streamingAssetsPath, configFileName);
            using var request = UnityWebRequest.Get(streamingPath);
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                json = request.downloadHandler.text;
            }
            else
            {
                Debug.LogError($"[AprilTagRoomLocalizer] Failed to load room config from {streamingPath}: {request.error}");
            }
        }

        if (string.IsNullOrEmpty(json))
        {
            yield break;
        }

        // Without this, a malformed config (e.g. a leading zero like "00", which
        // isn't valid JSON) throws inside the coroutine and silently kills Start(),
        // leaving nothing placed and no other sign of what failed.
        RoomConfig config;
        try
        {
            config = JsonUtility.FromJson<RoomConfig>(json);
        }
        catch (ArgumentException e)
        {
            Debug.LogError($"[AprilTagRoomLocalizer] Room config '{configFileName}' is not valid JSON - no tags will be recognized: {e.Message}");
            yield break;
        }

        if (config?.tags == null || config.tags.Length == 0)
        {
            Debug.LogError($"[AprilTagRoomLocalizer] Room config '{configFileName}' lists no tags - nothing can be recognized");
            yield break;
        }

        // No tag marked measured (older configs): treat them all as measured.
        var anyMeasured = Array.Exists(config.tags, t => t.measured);
        tagRegistry.Clear();
        foreach (var tag in config.tags)
        {
            var position = new Vector3(tag.x, tag.y, tag.z);
            var rotation = Quaternion.Euler(0, tag.yawDegrees, 0);
            tagRegistry[tag.id] = (position, rotation, tag.sizeMeters, tag.measured || !anyMeasured);
        }

        learnUnlistedTags = config.learnUnlistedTags && config.defaultTagSizeMeters > 0f;
        defaultTagSizeMeters = config.defaultTagSizeMeters;
        MeasuredTagsFingerprint = string.Join(";", tagRegistry.Where(t => t.Value.measured).OrderBy(t => t.Key)
            .Select(t => $"{t.Key}:{t.Value.position.x:F3},{t.Value.position.y:F3},{t.Value.position.z:F3},{t.Value.rotation.eulerAngles.y:F1}"));
        IsConfigLoaded = true;

        var measuredIds = string.Join(", ", config.tags.Where(t => tagRegistry[t.id].measured).Select(t => t.id));
        Debug.Log($"[AprilTagRoomLocalizer] Loaded room config: {tagRegistry.Count} listed tag(s) from {(File.Exists(overridePath) ? overridePath : "StreamingAssets")}; measured: {(anyMeasured ? measuredIds : "all (none marked)")}; " +
                  (learnUnlistedTags ? $"unlisted tags learned at {defaultTagSizeMeters * 100f:F2} cm" : "unlisted tags ignored"));
    }

    private void Update()
    {
        TrackRotationSpeed();

        if (pendingDetection != null && pendingDetection.IsCompleted)
        {
            ApplyDetection();
        }

        // The camera pauses with the app (e.g. headset taken off); querying it
        // then logs an error every frame.
        if (detector == null || frameInFlight || !cameraSource.IsPlaying)
        {
            return;
        }

        // Due time from the current interval, so a change (e.g. to fast while a
        // new tag needs samples) applies at once, not after the old interval.
        var wanted = armed || (continuousScan && Time.time >= lastScanRequestTime + scanIntervalSeconds);
        if (!wanted)
        {
            return;
        }

        if (timing.Frames > 0 && Time.time - lastTimingLogTime >= timingLogIntervalSeconds)
        {
            LogTiming(armed ? "acquiring" : "scanning");
        }

        // Set before requesting: a source may deliver the frame synchronously.
        frameInFlight = true;
        frameRequestTimestamp = Stopwatch.GetTimestamp();
        frameRequestTime = Time.time;
        frameRotationSpeed = rotationSpeed;
        lastScanRequestTime = Time.time;
        if (!cameraSource.TryRequestFrame(onFrameCaptured))
        {
            frameInFlight = false;
        }
    }

    // Main thread, when the source has the frame: hand it to a worker.
    private void OnFrameCaptured(bool succeeded, CameraFrame frame)
    {
        if (!succeeded || detector == null)
        {
            frameInFlight = false;
            return;
        }

        pendingFrame = frame;
        pendingPrepareMs = cameraSource.LastFramePrepareMilliseconds;
        // The frame's own intrinsics when the source has them (autofocus moves
        // the focal length several percent on iPhone); else the startup ones.
        pendingIntrinsics = frame.HasIntrinsics ? frame.Intrinsics : startupIntrinsics;
        if (Mathf.Abs(pendingIntrinsics.Fx - lastLoggedFx) > 0.005f * lastLoggedFx)
        {
            Debug.Log($"[AprilTagRoomLocalizer] Camera focal length changed: fx {lastLoggedFx:F1} -> {pendingIntrinsics.Fx:F1} ({(pendingIntrinsics.Fx / lastLoggedFx - 1f) * 100f:+0.0;-0.0}%)");
            lastLoggedFx = pendingIntrinsics.Fx;
        }
        var image = frame.Image;
        var intrinsics = pendingIntrinsics;
        var delivered = Stopwatch.GetTimestamp();
        pendingDetection = Task.Run(() => Detect(image, intrinsics, delivered));
    }

    private readonly struct CameraTagSample
    {
        public readonly int TagId;
        public readonly bool Listed;
        public readonly Vector2 PixelCenter;
        public readonly Vector3 PositionCameraLocal;
        public readonly Quaternion RotationCameraLocal;

        public CameraTagSample(int tagId, bool listed, Vector2 pixelCenter, Vector3 positionCameraLocal, Quaternion rotationCameraLocal)
        {
            TagId = tagId;
            Listed = listed;
            PixelCenter = pixelCenter;
            PositionCameraLocal = positionCameraLocal;
            RotationCameraLocal = rotationCameraLocal;
        }
    }

    private sealed class DetectionResult
    {
        public readonly List<CameraTagSample> Samples = new();
        public long DeliveredTimestamp;
        public double DetectMs;
        public double PoseMs;
    }

    // Worker thread: detect, then estimate each known tag's pose relative to the
    // camera. Uses only the detector (never called concurrently) and read-only
    // state set up before detection started.
    private DetectionResult Detect(ImageU8 image, PinholeIntrinsics intrinsics, long deliveredTimestamp)
    {
        var result = new DetectionResult { DeliveredTimestamp = deliveredTimestamp };
        var start = Stopwatch.GetTimestamp();
        using var detections = detector.Detect(image);
        var detected = Stopwatch.GetTimestamp();
        result.DetectMs = Milliseconds(start, detected);

        for (var i = 0; i < detections.Length; i++)
        {
            ref var det = ref detections[i];
            var listed = tagRegistry.TryGetValue(det.ID, out var known);
            // Unlisted tags only if the config asks for them, and only perfect
            // decodes (no corrected bit errors), to keep stray IDs out.
            if (!listed && (!learnUnlistedTags || det.Hamming != 0))
            {
                continue;
            }
            var sizeMeters = listed ? known.sizeMeters : defaultTagSizeMeters;

            // Only trust detections within the angular range we've actually
            // validated with ground-truth testing - accuracy degrades at steeper
            // off-axis angles. Users should look directly at the tag to acquire.
            var pixelOffsetX = (float)det.Center.x - intrinsics.Cx;
            var pixelOffsetY = (float)det.Center.y - intrinsics.Cy;
            var angleXDegrees = Mathf.Atan2(pixelOffsetX, intrinsics.Fx) * Mathf.Rad2Deg;
            var angleYDegrees = Mathf.Atan2(pixelOffsetY, intrinsics.Fy) * Mathf.Rad2Deg;
            if (Mathf.Abs(angleXDegrees) > maxOffAxisAngleDegrees || Mathf.Abs(angleYDegrees) > maxOffAxisAngleDegrees)
            {
                continue;
            }

            var info = new DetectionInfo(
                ref det, sizeMeters,
                intrinsics.Fx, intrinsics.Fy,
                intrinsics.Cx, intrinsics.Cy);

            using var pose = new AprilTag.Interop.Pose(ref info);

            // Same CV-to-Unity axis conversion as the package's own PoseEstimationJob,
            // validated correct earlier via controlled physical rotation tests.
            var rawPos = math.float3((float)pose.t.e0, (float)pose.t.e1, (float)pose.t.e2);
            var tagPosition = rawPos * math.float3(1, -1, 1);

            var rawRot = math.float3x3(
                (float)pose.R.e00, (float)pose.R.e01, (float)pose.R.e02,
                (float)pose.R.e10, (float)pose.R.e11, (float)pose.R.e12,
                (float)pose.R.e20, (float)pose.R.e21, (float)pose.R.e22);
            var rawQuat = math.quaternion(rawRot);
            var flippedQuat = rawQuat.value * math.float4(-1, 1, -1, 1);

            result.Samples.Add(new CameraTagSample(det.ID, listed,
                new Vector2((float)det.Center.x, (float)det.Center.y),
                new Vector3(tagPosition.x, tagPosition.y, tagPosition.z),
                new Quaternion(flippedQuat.x, flippedQuat.y, flippedQuat.z, flippedQuat.w)));
        }
        result.PoseMs = Milliseconds(detected, Stopwatch.GetTimestamp());
        return result;
    }

    // Main thread: turn the worker's camera-relative samples into world-space
    // observations, report them, and feed any acquisition in progress.
    private void ApplyDetection()
    {
        var task = pendingDetection;
        pendingDetection = null;
        frameInFlight = false;

        if (task.IsFaulted)
        {
            Debug.LogError($"[AprilTagRoomLocalizer] Detection failed: {task.Exception?.GetBaseException()}");
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var result = task.Result;
        var frame = pendingFrame;
        var rejected = maxRotationSpeedDegreesPerSecond > 0f && frameRotationSpeed > maxRotationSpeedDegreesPerSecond;
        foreach (var sample in rejected ? new List<CameraTagSample>() : result.Samples)
        {
            var sessionTagPosition = frame.CameraPosition + frame.CameraRotation * sample.PositionCameraLocal;
            var sessionTagRotation = frame.CameraRotation * sample.RotationCameraLocal;
            if (!sample.Listed)
            {
                TagObserved?.Invoke(new TagObservation(sample.TagId, false, false, frameRequestTime, sample.PositionCameraLocal.magnitude,
                    frameRotationSpeed, frame.CameraPosition, Vector3.zero, sessionTagPosition, sessionTagPosition, Quaternion.identity));
                continue;
            }
            var known = tagRegistry[sample.TagId];

            // Solve for the room origin's pose in this session's world space, given
            // this tag's known fixed pose in room coordinates and its just-detected
            // pose in this session: sessionTagPose = roomOriginPose * knownRoomPose.
            // Both the room frame and the headset's tracking frame share the same
            // true "up" (gravity), so the room origin's rotation relative to this
            // session should be yaw-only - any pitch/roll here is just noise from
            // the angle the tag happened to be viewed at, and must be discarded.
            var rawRelativeRotation = sessionTagRotation * Quaternion.Inverse(known.rotation);
            var flatForward = Vector3.ProjectOnPlane(rawRelativeRotation * Vector3.forward, Vector3.up);
            var originRotation = Quaternion.LookRotation(flatForward, Vector3.up);
            var originPosition = sessionTagPosition - originRotation * known.position;

            TagObserved?.Invoke(new TagObservation(sample.TagId, known.measured, true, frameRequestTime, sample.PositionCameraLocal.magnitude,
                frameRotationSpeed, frame.CameraPosition, known.position, sessionTagPosition, originPosition, originRotation));

            if (armed && AddAcquisitionSample(sample, known.position, sessionTagPosition, originPosition, originRotation))
            {
                break;
            }
        }

        var end = Stopwatch.GetTimestamp();
        timing.Add(pendingPrepareMs, result.DetectMs, result.PoseMs, Milliseconds(start, end),
            Milliseconds(frameRequestTimestamp, result.DeliveredTimestamp), Milliseconds(frameRequestTimestamp, end),
            frameRotationSpeed, result.Samples.Count > 0, rejected && result.Samples.Count > 0);
    }

    // Accumulates one sample into the acquisition window; true if it completed
    // the window and an estimate was raised.
    private bool AddAcquisitionSample(CameraTagSample sample, Vector3 tagRoomPosition, Vector3 sessionTagPosition,
        Vector3 sampleRoomOriginPosition, Quaternion sampleRoomOriginRotation)
    {
        // A window only averages samples of the tag that started it, so a
        // second tag in view can't blend its own systematic error in.
        if (acquiring && sample.TagId != acquiringTagId && Time.time - lastSampleTime <= maxSampleGapSeconds)
        {
            return false;
        }

        // Accumulate samples over a short window rather than trusting the very
        // first detection - smooths out both single-frame noise and any
        // transient tracking disturbance from recent head movement. The window
        // must be a continuous run of sightings: if the tag drops out for longer
        // than maxSampleGapSeconds, start over rather than averaging a sample
        // from long ago with one from now.
        if (acquiring && Time.time - lastSampleTime > maxSampleGapSeconds)
        {
            Debug.Log($"[AprilTagRoomLocalizer] Lost tag {acquiringTagId} for {Time.time - lastSampleTime:F2}s after {sampleCount} sample(s); restarting acquisition");
            acquiring = false;
        }

        if (!acquiring)
        {
            acquiring = true;
            acquiringTagId = sample.TagId;
            firstSampleTime = Time.time;
            positionSum = Vector3.zero;
            forwardSum = Vector3.zero;
            pixelCenterSum = Vector2.zero;
            cameraLocalPositionSum = Vector3.zero;
            tagWorldPositionSum = Vector3.zero;
            sampleCount = 0;
            sampleTagPositions.Clear();
            sampleOriginPositions.Clear();
            sampleYaws.Clear();
        }

        positionSum += sampleRoomOriginPosition;
        forwardSum += sampleRoomOriginRotation * Vector3.forward;
        pixelCenterSum += sample.PixelCenter;
        cameraLocalPositionSum += sample.PositionCameraLocal;
        tagWorldPositionSum += sessionTagPosition;
        sampleCount++;
        sampleTagPositions.Add(sessionTagPosition);
        sampleOriginPositions.Add(sampleRoomOriginPosition);
        sampleYaws.Add(sampleRoomOriginRotation.eulerAngles.y);
        lastSampleTime = Time.time;

        if (Time.time - firstSampleTime < acquisitionWindowSeconds)
        {
            return false;
        }

        var estimate = new RoomOriginEstimate(
            sample.TagId,
            positionSum / sampleCount,
            Quaternion.LookRotation(forwardSum.normalized, Vector3.up),
            sampleCount,
            cameraLocalPositionSum / sampleCount,
            tagWorldPositionSum / sampleCount,
            tagRoomPosition);
        var avgPixelCenter = pixelCenterSum / sampleCount;

        armed = false;
        acquiring = false;

        Debug.Log($"[AprilTagRoomLocalizer] Locked using tag {sample.TagId} {Time.time - armedTime:F1}s after acquisition began, averaged {sampleCount} samples over {Time.time - firstSampleTime:F1}s; avg tag pixel center=({avgPixelCenter.x:F1},{avgPixelCenter.y:F1}) principalPoint=({principalPointCx:F1},{principalPointCy:F1}); avg tagPositionCameraLocal={estimate.TagPositionCameraLocal}; room origin at world pos={estimate.Position} rot={estimate.Rotation.eulerAngles}");
        Debug.Log($"[AprilTagRoomLocalizer] Per-frame spread over {sampleCount} samples (tag {sample.TagId}, {estimate.TagPositionCameraLocal.magnitude:F2} m): " +
                  $"tag position {PositionSpread(sampleTagPositions)}; room origin {PositionSpread(sampleOriginPositions)}; yaw {YawSpread(sampleYaws, estimate.Rotation.eulerAngles.y)}");
        LogTiming("locked");
        EstimateAcquired?.Invoke(estimate);
        return true;
    }

    // Standard deviation per axis and of the 3D distance from the mean, plus the
    // largest single-sample distance, in cm.
    private static string PositionSpread(List<Vector3> samples)
    {
        var mean = Vector3.zero;
        foreach (var p in samples) mean += p;
        mean /= samples.Count;
        var sq = Vector3.zero;
        float sqDist = 0f, maxDist = 0f;
        foreach (var p in samples)
        {
            var d = p - mean;
            sq += Vector3.Scale(d, d);
            sqDist += d.sqrMagnitude;
            maxDist = Mathf.Max(maxDist, d.magnitude);
        }
        var sd = new Vector3(Mathf.Sqrt(sq.x / samples.Count), Mathf.Sqrt(sq.y / samples.Count), Mathf.Sqrt(sq.z / samples.Count)) * 100f;
        return $"sd ({sd.x:F2}, {sd.y:F2}, {sd.z:F2}) cm, 3D {Mathf.Sqrt(sqDist / samples.Count) * 100f:F2} cm, max {maxDist * 100f:F2} cm";
    }

    // Standard deviation and largest deviation of sample yaws about the mean, in degrees.
    private static string YawSpread(List<float> yaws, float meanYaw)
    {
        float sq = 0f, max = 0f;
        foreach (var yaw in yaws)
        {
            var d = Mathf.Abs(Mathf.DeltaAngle(meanYaw, yaw));
            sq += d * d;
            max = Mathf.Max(max, d);
        }
        return $"sd {Mathf.Sqrt(sq / yaws.Count):F2} deg, max {max:F2} deg";
    }

    // Rotation speed of the main camera (the headset or phone), lightly smoothed.
    private void TrackRotationSpeed()
    {
        if (rotationReference == null)
        {
            rotationReference = Camera.main != null ? Camera.main.transform : null;
            if (rotationReference == null)
            {
                return;
            }
        }
        var now = Time.unscaledTime;
        var rotation = rotationReference.rotation;
        if (lastRotationTime >= 0f && now > lastRotationTime)
        {
            // Not Quaternion.Angle: it returns 0 below ~0.16 deg, i.e. under ~10 deg/s
            // at 60 fps. The rotation between frames, accurate for tiny angles:
            var delta = Quaternion.Inverse(lastRotation) * rotation;
            var vector = new Vector3(delta.x, delta.y, delta.z).magnitude;
            var angle = 2f * Mathf.Atan2(vector, Mathf.Abs(delta.w)) * Mathf.Rad2Deg;
            var speed = angle / (now - lastRotationTime);
            rotationSpeed = Mathf.Lerp(rotationSpeed, speed, 0.5f);
        }
        lastRotation = rotation;
        lastRotationTime = now;
    }

    private static double Milliseconds(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    private void LogTiming(string reason)
    {
        if (timing.Frames > 0)
        {
            Debug.Log($"[AprilTagRoomLocalizer] Timing ({reason}): {timing.Summary(Time.realtimeSinceStartup)}");
        }
        timing.Reset(Time.realtimeSinceStartup);
        lastTimingLogTime = Time.time;
    }

    // Per-frame cost. prepare (image copy/convert) and apply run on the main
    // thread; detect and pose on a worker. latency is from requesting a frame to
    // receiving it (includes any async GPU readback); total is request to applied.
    private sealed class FrameTiming
    {
        private float startTime;
        private double prepareSum, prepareMax, detectSum, detectMax, poseSum, poseMax, applySum, applyMax;
        private double latencySum, latencyMax, totalSum, totalMax;
        private double rotationSum, rotationMax;
        private int framesWithTags, rejectedWithTags;

        public int Frames { get; private set; }

        public void Reset(float now)
        {
            startTime = now;
            Frames = 0;
            prepareSum = prepareMax = detectSum = detectMax = poseSum = poseMax = applySum = applyMax = 0;
            latencySum = latencyMax = totalSum = totalMax = 0;
            rotationSum = rotationMax = 0;
            framesWithTags = rejectedWithTags = 0;
        }

        public void Add(double prepareMs, double detectMs, double poseMs, double applyMs, double latencyMs, double totalMs,
            float rotationSpeed, bool hadTags, bool rejected)
        {
            Frames++;
            rotationSum += rotationSpeed; rotationMax = Math.Max(rotationMax, rotationSpeed);
            framesWithTags += hadTags ? 1 : 0;
            rejectedWithTags += rejected ? 1 : 0;
            prepareSum += prepareMs; prepareMax = Math.Max(prepareMax, prepareMs);
            detectSum += detectMs; detectMax = Math.Max(detectMax, detectMs);
            poseSum += poseMs; poseMax = Math.Max(poseMax, poseMs);
            applySum += applyMs; applyMax = Math.Max(applyMax, applyMs);
            latencySum += latencyMs; latencyMax = Math.Max(latencyMax, latencyMs);
            totalSum += totalMs; totalMax = Math.Max(totalMax, totalMs);
        }

        public string Summary(float now)
        {
            var seconds = Math.Max(now - startTime, 1e-3f);
            return $"{Frames} frames in {seconds:F1}s ({Frames / seconds:F1}/s); ms mean/max: " +
                   $"main thread: prepare {prepareSum / Frames:F1}/{prepareMax:F1}, apply {applySum / Frames:F2}/{applyMax:F2}; " +
                   $"worker: detect {detectSum / Frames:F1}/{detectMax:F1}, pose {poseSum / Frames:F2}/{poseMax:F2}; " +
                   $"latency {latencySum / Frames:F1}/{latencyMax:F1}, request to applied {totalSum / Frames:F1}/{totalMax:F1}; " +
                   $"rotation {rotationSum / Frames:F1}/{rotationMax:F1} deg/s, {rejectedWithTags} of {framesWithTags} frames with tags dropped for turning";
        }
    }

    private void OnDestroy()
    {
        // Let an in-flight detection finish before the detector goes away.
        try
        {
            pendingDetection?.Wait(500);
        }
        catch (AggregateException)
        {
        }
        if (detector != null && family != null)
        {
            detector.RemoveFamily(family);
        }
        family?.Dispose();
        detector?.Dispose();
    }
}
