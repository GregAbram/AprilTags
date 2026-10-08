using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

// Detects the tags listed in the room config and solves for the room origin's
// pose in this session. Each BeginAcquisition() produces one estimate - from
// whichever known tag first stays in view for a full acquisition window - and
// raises EstimateAcquired; detection is idle between acquisitions. Frames come
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

    [Tooltip("While acquiring, log per-frame timing (image prepare, detection, processing, latency) this often; also logged when an acquisition ends.")]
    [SerializeField] private float timingLogIntervalSeconds = 5f;

    public event Action<RoomOriginEstimate> EstimateAcquired;

    // True from BeginAcquisition() until an estimate is raised or cancelled,
    // including while still starting up (config load, camera, settle delay).
    public bool IsAcquiring => armed;
    public bool IsReady => detector != null;
    public IReadOnlyCollection<int> KnownTagIds => tagRegistry.Keys;

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
    private bool frameInFlight;
    private long frameRequestTimestamp;
    private readonly FrameTiming timing = new();
    private float lastTimingLogTime;
    private Action<bool, CameraFrame> onFrameCaptured;
    private float principalPointCx;
    private float principalPointCy;
    private float focalLengthFx;
    private float focalLengthFy;
    private readonly Dictionary<int, (Vector3 position, Quaternion rotation, float sizeMeters)> tagRegistry = new();

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
        timing.Reset(Time.realtimeSinceStartup);
        lastTimingLogTime = Time.time;
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

        Debug.Log($"[AprilTagRoomLocalizer] Camera intrinsics {intrinsics}");
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

        tagRegistry.Clear();
        foreach (var tag in config.tags)
        {
            var position = new Vector3(tag.x, tag.y, tag.z);
            var rotation = Quaternion.Euler(0, tag.yawDegrees, 0);
            tagRegistry[tag.id] = (position, rotation, tag.sizeMeters);
        }

        Debug.Log($"[AprilTagRoomLocalizer] Loaded room config: {tagRegistry.Count} known tag(s) from {(File.Exists(overridePath) ? overridePath : "StreamingAssets")}");
    }

    private void Update()
    {
        // The camera pauses with the app (e.g. headset taken off); querying it
        // then logs an error every frame.
        if (detector == null || frameInFlight || !armed || !cameraSource.IsPlaying)
        {
            return;
        }

        if (Time.time - lastTimingLogTime >= timingLogIntervalSeconds)
        {
            LogTiming("acquiring");
            lastTimingLogTime = Time.time;
        }

        // Set before requesting: a source may deliver the frame synchronously.
        frameInFlight = true;
        frameRequestTimestamp = Stopwatch.GetTimestamp();
        if (!cameraSource.TryRequestFrame(onFrameCaptured))
        {
            frameInFlight = false;
        }
    }

    private void OnFrameCaptured(bool succeeded, CameraFrame frame)
    {
        frameInFlight = false;

        if (!armed || !succeeded || detector == null)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var latencyMs = Milliseconds(frameRequestTimestamp, start);
        using var detections = detector.Detect(frame.Image);
        var detectMs = Milliseconds(start, Stopwatch.GetTimestamp());
        try
        {
            ProcessDetections(detections, frame);
        }
        finally
        {
            var end = Stopwatch.GetTimestamp();
            timing.Add(cameraSource.LastFramePrepareMilliseconds, detectMs, Milliseconds(start, end) - detectMs, latencyMs);
        }
    }

    private void ProcessDetections(DetectionArray detections, CameraFrame frame)
    {
        for (var i = 0; i < detections.Length; i++)
        {
            ref var det = ref detections[i];
            if (!tagRegistry.TryGetValue(det.ID, out var knownRoomPose))
            {
                continue;
            }

            // A window only averages samples of the tag that started it, so a
            // second tag in view can't blend its own systematic error in.
            if (acquiring && det.ID != acquiringTagId && Time.time - lastSampleTime <= maxSampleGapSeconds)
            {
                continue;
            }

            // Only trust detections within the angular range we've actually
            // validated with ground-truth testing - accuracy degrades at steeper
            // off-axis angles. Users should look directly at the tag to acquire.
            var pixelOffsetX = (float)det.Center.x - principalPointCx;
            var pixelOffsetY = (float)det.Center.y - principalPointCy;
            var angleXDegrees = Mathf.Atan2(pixelOffsetX, focalLengthFx) * Mathf.Rad2Deg;
            var angleYDegrees = Mathf.Atan2(pixelOffsetY, focalLengthFy) * Mathf.Rad2Deg;
            if (Mathf.Abs(angleXDegrees) > maxOffAxisAngleDegrees || Mathf.Abs(angleYDegrees) > maxOffAxisAngleDegrees)
            {
                continue;
            }

            var info = new DetectionInfo(
                ref det, knownRoomPose.sizeMeters,
                focalLengthFx, focalLengthFy,
                principalPointCx, principalPointCy);

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

            var tagPositionCameraLocal = new Vector3(tagPosition.x, tagPosition.y, tagPosition.z);
            var tagRotationCameraLocal = new Quaternion(flippedQuat.x, flippedQuat.y, flippedQuat.z, flippedQuat.w);

            var sessionTagPosition = frame.CameraPosition + frame.CameraRotation * tagPositionCameraLocal;
            var sessionTagRotation = frame.CameraRotation * tagRotationCameraLocal;

            // Solve for the room origin's pose in this session's world space, given
            // this tag's known fixed pose in room coordinates and its just-detected
            // pose in this session: sessionTagPose = roomOriginPose * knownRoomPose.
            // Both the room frame and the headset's tracking frame share the same
            // true "up" (gravity), so the room origin's rotation relative to this
            // session should be yaw-only - any pitch/roll here is just noise from
            // the angle the tag happened to be viewed at, and must be discarded.
            var rawRelativeRotation = sessionTagRotation * Quaternion.Inverse(knownRoomPose.rotation);
            var flatForward = Vector3.ProjectOnPlane(rawRelativeRotation * Vector3.forward, Vector3.up);
            var sampleRoomOriginRotation = Quaternion.LookRotation(flatForward, Vector3.up);
            var sampleRoomOriginPosition = sessionTagPosition - sampleRoomOriginRotation * knownRoomPose.position;

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
                acquiringTagId = det.ID;
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
            pixelCenterSum += new Vector2((float)det.Center.x, (float)det.Center.y);
            cameraLocalPositionSum += tagPositionCameraLocal;
            tagWorldPositionSum += sessionTagPosition;
            sampleCount++;
            sampleTagPositions.Add(sessionTagPosition);
            sampleOriginPositions.Add(sampleRoomOriginPosition);
            sampleYaws.Add(sampleRoomOriginRotation.eulerAngles.y);
            lastSampleTime = Time.time;

            if (Time.time - firstSampleTime < acquisitionWindowSeconds)
            {
                break;
            }

            var estimate = new RoomOriginEstimate(
                det.ID,
                positionSum / sampleCount,
                Quaternion.LookRotation(forwardSum.normalized, Vector3.up),
                sampleCount,
                cameraLocalPositionSum / sampleCount,
                tagWorldPositionSum / sampleCount,
                knownRoomPose.position);
            var avgPixelCenter = pixelCenterSum / sampleCount;

            armed = false;
            acquiring = false;

            Debug.Log($"[AprilTagRoomLocalizer] Locked using tag {det.ID} {Time.time - armedTime:F1}s after acquisition began, averaged {sampleCount} samples over {Time.time - firstSampleTime:F1}s; avg tag pixel center=({avgPixelCenter.x:F1},{avgPixelCenter.y:F1}) principalPoint=({principalPointCx:F1},{principalPointCy:F1}); avg tagPositionCameraLocal={estimate.TagPositionCameraLocal}; room origin at world pos={estimate.Position} rot={estimate.Rotation.eulerAngles}");

            Debug.Log($"[AprilTagRoomLocalizer] Per-frame spread over {sampleCount} samples (tag {det.ID}, {estimate.TagPositionCameraLocal.magnitude:F2} m): " +
                      $"tag position {PositionSpread(sampleTagPositions)}; room origin {PositionSpread(sampleOriginPositions)}; yaw {YawSpread(sampleYaws, estimate.Rotation.eulerAngles.y)}");
            LogTiming("locked");
            EstimateAcquired?.Invoke(estimate);
            break;
        }
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

    private static double Milliseconds(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    private void LogTiming(string reason)
    {
        if (timing.Frames > 0)
        {
            Debug.Log($"[AprilTagRoomLocalizer] Timing ({reason}): {timing.Summary(Time.realtimeSinceStartup)}");
        }
    }

    // Per-frame cost while acquiring. prepare, detect and process run on the
    // main thread; latency is from requesting a frame to receiving it (includes
    // any async GPU readback).
    private sealed class FrameTiming
    {
        private float startTime;
        private double prepareSum, prepareMax, detectSum, detectMax, processSum, processMax, latencySum, latencyMax;

        public int Frames { get; private set; }

        public void Reset(float now)
        {
            startTime = now;
            Frames = 0;
            prepareSum = prepareMax = detectSum = detectMax = processSum = processMax = latencySum = latencyMax = 0;
        }

        public void Add(double prepareMs, double detectMs, double processMs, double latencyMs)
        {
            Frames++;
            prepareSum += prepareMs; prepareMax = Math.Max(prepareMax, prepareMs);
            detectSum += detectMs; detectMax = Math.Max(detectMax, detectMs);
            processSum += processMs; processMax = Math.Max(processMax, processMs);
            latencySum += latencyMs; latencyMax = Math.Max(latencyMax, latencyMs);
        }

        public string Summary(float now)
        {
            var seconds = Math.Max(now - startTime, 1e-3f);
            var mainThread = (prepareSum + detectSum + processSum) / Frames;
            return $"{Frames} frames in {seconds:F1}s ({Frames / seconds:F1}/s); ms mean/max: " +
                   $"prepare {prepareSum / Frames:F1}/{prepareMax:F1}, detect {detectSum / Frames:F1}/{detectMax:F1}, " +
                   $"process {processSum / Frames:F2}/{processMax:F2}, main thread total {mainThread:F1}; " +
                   $"latency {latencySum / Frames:F1}/{latencyMax:F1}";
        }
    }

    private void OnDestroy()
    {
        if (detector != null && family != null)
        {
            detector.RemoveFamily(family);
        }
        family?.Dispose();
        detector?.Dispose();
    }
}
