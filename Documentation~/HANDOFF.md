# Handoff: AprilTag colocation — state as of 2026-10-05

Context for continuing this work in a new session (written at the end of a long
Quest 3 session on Windows; next step is an iPhone port on a Mac). Read this
before touching code — several conventions below were learned the hard way and
contradict the README.

## Repositories and projects

| What | Where | Notes |
|---|---|---|
| This package (`edu.tacc.apriltags`) | github.com/GregAbram/AprilTags | Up to date on GitHub. |
| AprilTag detector (`edu.umn.cs.ivlab.apriltag`) | github.com/GregAbram/AprilTag-UnityPackage | Fork of ivlab's. Consume pinned: `https://github.com/GregAbram/AprilTag-UnityPackage.git#82a5ae5ed072011fe0bf47f80ebdb92efad4a6fd` |
| Quest test app "AprilTags2" | Windows: `~/Unity/AprilTags2` | Local git repo only, **not on GitHub yet**. Unity 6000.3.10f1, app id `edu.utexas.tacc.apriltags2`. |
| Original Quest app "AprilTag" | Windows: `~/Unity/AprilTag` | Not in git. Its `room_config.json` still has the wrong sizes/yaws (see below). |

AprilTags2 consumes this package by local path
(`file:C:/Users/gda/Documents/GitHub/AprilTagColocation-UnityPackage`); on the
Mac, use a git URL or a local path to a clone.

## Package code (Runtime/)

- `AprilTagRoomLocalizer` — loads `room_config.json`, runs detection on
  `PassthroughCameraAccess` frames, averages a 1 s window of one tag, and raises
  `EstimateAcquired(RoomOriginEstimate)` once per `BeginAcquisition()`.
  Restarts the window if the tag is unseen > 0.25 s; ignores detections > 20°
  off the image center; waits 2 s after start for tracking to settle.
- `RoomOriginEstimate` — room origin pose in session world space from one tag,
  plus that tag's averaged world position and configured room position.
- `RoomFit` — best-fit room pose (yaw + translation; gravity fixes up) from
  **several tags' positions only**, ignoring each tag's own orientation.
  Weighted 2D Kabsch/Procrustes in the horizontal plane; returns per-tag
  residuals in room axes. Verified on synthetic data (exact without noise;
  ≤0.52° yaw error with 1 cm noise on this room's three tags). Verified on
  Quest 3 on 2026-10-06 (see "Measured results").
- `TextureIntrinsics` — maps Meta's sensor intrinsics onto the delivered texture
  (Quest-specific; see "Intrinsics" below).
- `AprilTagBoxPlacement` — thin backward-compatible wrapper: one box at a room
  pose from the first tag after startup. Adds/configures a localizer if absent.
- `RoomConfig` — JSON schema.

The detector package's own `TagDetector` (Unity layer) is deliberately **not**
used: it models the camera with a single FOV and assumes a centered principal
point. We call its `Interop` layer directly (`Detector`, `Family`, `ImageU8`,
`DetectionInfo`, `Pose`) with explicit fx/fy/cx/cy.

## room_config.json conventions (verified on device)

1. **Family:** only `tagStandard41h12` is detected. tag36h11 (the common
   default) is silently never found.
2. **`sizeMeters` is the black border, = 5/9 of the printed tag's width.** In
   41h12 the black border ring spans 5 of the 9 cells; the outermost data ring
   is outside it. Our tags are 17 cm printed → **0.0944**. Using 0.0639 (an old
   fudge value) made distances read ~66% of true (0.66 m at a measured 1.00 m).
   *README says "solid black border" — correct but easily misread; it needs the
   5/9 rule spelled out.*
3. **Room frame is left-handed, like Unity:** +X right, +Y up (gravity),
   +Z forward. Stand facing your "front" wall with +X to your right, and +Z is
   straight ahead into that wall. Measuring X/Z along walls the other way gives
   a mirrored room that no rotation can fix (this happened once).
4. **`yawDegrees` = the direction you face when looking straight at the tag**
   (the tag's +Z points into its wall). Front wall = 0. *README says "outward-
   facing normal" — that is wrong.* Caveat: in this room tag 7 needed 270 and
   tag 9 needed 90 although their walls' positions suggested the opposite; the
   cause was never pinned down, so verify each side-wall tag empirically: a
   180° yaw error shows up as the origin mirrored through that tag (cube appears
   twice as far away, beyond the tag).
5. Only pure yaw: mount tags flat and upright on vertical walls.
6. `room` width/depth/height are unused by the code.
7. **JSON must be strict:** `00` (leading zero) is invalid. This used to fail
   silently and stop all detection; it now logs a clear error.

Current AprilTags2 config (origin = where old tag 6 was, on the front wall low
down; tags 7 and 9 x-values were **fitted** from one run, not tape-measured —
the fit implies the room is 4.46 m wall-to-wall vs 4.61 m measured):

```json
{ "id": 9, "x": -4.351, "y": 0.91, "z": -2.460, "yawDegrees": 90,  "sizeMeters": 0.0944 },
{ "id": 8, "x": -1.11,  "y": 1.39, "z": -0.015, "yawDegrees": 0,   "sizeMeters": 0.0944 },
{ "id": 7, "x":  0.105, "y": 1.43, "z": -2.240, "yawDegrees": 270, "sizeMeters": 0.0944 }
```

## Measured results on Quest 3

- Single tag at the origin: cube lands on the tag after the size fix.
- Three tags, per-tag origin estimates (difference from tag 8, room axes):
  - tape-measured config: tag 7 (+10.5, −0.7, −0.9) cm; tag 9 (+25.9, −0.3, −0.6) cm
  - fitted config: tag 7 (2.2, −0.6, −1.5) cm; tag 9 (1.7, −0.6, −4.5) cm
  - run-to-run repeatability ~3–4 cm; Y/Z agree within ~1 cm.
- Single-tag yaw is the weak link: typically ~1° off, once **12.6°** (tag seen
  far away and well below image center). 1° of yaw ≈ 4 cm of origin error per
  2.2 m of tag-to-origin distance — the motivation for `RoomFit`.

**RoomFit on Quest 3 (2026-10-06, package 0.2 with `PassthroughCameraSource`,
fitted config above):** the camera-source refactor left intrinsics and
detection unchanged. Per-tag differences from tag 8: tag 7 (−0.1, −0.7, −2.1) cm,
tag 9 (−1.7, −1.4, −8.0) cm (tag 9's 8 cm is mostly its 1.3° yaw error). The
3-tag fit landed within 1.7 cm of tag 8's estimate, yaw within 0.5°, RMS
residual 4.5 cm; residuals tag 7 +5.5 cm x, tag 8 0.9 cm, tag 9 −5.3 cm x.
So the position-only fit sees tags 7 and 9 ~10.8 cm further apart than the
fitted config: the room is ~4.56 m wide, not 4.46 m. The single-tag "fit" that
produced the config was itself biased by yaw errors. A ~1% depth under-read
(hinted at by the 1 m test) would account for ~5 cm of that, putting the true
width near the tape's 4.61 m. User judged this good enough; the tape-measured
re-test was not done.

## Pitfalls already hit

- **Runtime-created objects render pink and differently per eye** in a URP
  single-pass-stereo build: `GameObject.CreatePrimitive` at runtime gets the
  built-in default material. Always assign a URP material that the scene
  references (AprilTags2 uses `Assets/Materials/TagCube.mat`, URP Unlit) so the
  shader and its stereo variants survive build stripping. Likely relevant on
  iOS too (no stereo, but the shader-stripping half still applies).
- **Quest intrinsics:** `SensorResolution` reports 1280×1280 while the texture
  is 1280×960. The texture is a **center crop** (160 px top/bottom) at scale 1,
  not a resize — mirrors MRUK's `CalcSensorCropRegion`. Sensor coords are
  bottom-origin; the detector image (after our vertical flip) is top-origin,
  so cy is flipped. Result on device: fx=fy≈866.6, cx≈643.7, cy≈478.6.
- **Readback orientation:** `ConvertToImageU8` flips rows (Unity textures are
  bottom-up) and uses the green channel as grayscale. A wrongly oriented image
  makes tags undetectable (mirrored codes don't decode), so if nothing is ever
  detected on a new platform, suspect orientation first.
- Capture the camera pose when the frame is requested, not when the async
  readback completes.
- Querying `PassthroughCameraAccess` while paused (headset removed) spams errors;
  the localizer now skips frames while `!IsPlaying`.

## Debugging workflow that worked (Quest)

- `adb` is at `<Unity>/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/`.
- Logs: `adb logcat | grep "\[AprilTag"` — prefixes `[AprilTagRoomLocalizer]`,
  `[AprilTagMultiCubes]`, `[AprilTagBoxPlacement]`. The lock line includes the
  tag's camera-local position (z = distance), pixel center, intrinsics.
- Swap configs without rebuilding: push to
  `/sdcard/Android/data/<app id>/files/room_config.json` (overrides the
  StreamingAssets copy; delete it to revert). On iOS the equivalent override
  location is `Application.persistentDataPath` (app Documents; reachable via
  Xcode → Devices → Download/Replace Container, or enable file sharing).
- Distance sanity check: stand with the headset front exactly 1.00 m from a tag;
  logged z should be ≈0.98–1.0.

## iPhone port plan

**Status (2026-10-05, Mac):** steps 1 and 4 are done — `AprilTagCameraSource`
(abstract component) + `PassthroughCameraSource` (Quest, same behavior as
before), and the Meta code now lives in `Runtime/Quest` (asmdef
`TaccAprilTags.Quest`, compiled only when `com.meta.xr.mrutilitykit` is
installed). Verified by headless compile: core alone for iOS, core + Quest with
MRUK 203. **Not yet re-tested on Quest hardware.** AprilTags2 scenes that place
`AprilTagRoomLocalizer` directly must add a `PassthroughCameraSource`. Next:
an ARKit source (step 2) and an iOS test app (step 3).

The detector already ships an iOS arm64 static library
(`Plugin/iOS/libAprilTag.a`) — no native rebuild needed. Port only what is
Quest-specific:

1. **Camera source abstraction.** Introduce an interface the localizer uses
   instead of `PassthroughCameraAccess` directly: is-playing, latest frame (CPU
   grayscale or texture), intrinsics in that frame's pixel space, camera pose at
   capture time. Quest implementation wraps `PassthroughCameraAccess` +
   `TextureIntrinsics` (keep current behavior bit-for-bit).
2. **iOS implementation with AR Foundation + ARKit**:
   `ARCameraManager.TryAcquireLatestCpuImage` (convert the Y plane straight to
   `ImageU8` — it's already grayscale; no GPU readback needed),
   `ARCameraManager.TryGetIntrinsics` (reported for the CPU image resolution,
   so no crop mapping), and the AR camera's world pose at frame time
   (`frameReceived` timing). Check image orientation: the CPU image is in
   sensor (landscape) orientation regardless of device orientation, and its row
   order may not need our vertical flip — verify by whether tags detect.
3. **Scene:** AR Session + XR Origin (AR camera background) instead of
   OVRCameraRig/passthrough. Input: screen tap instead of `OVRInput`. Keep
   `RoomFit` and the multi-cube comparison app logic; it's platform-neutral
   except input and haptics.
4. **Packages:** AR Foundation + Apple ARKit XR Plugin; iOS Build Support
   module. The package's asmdef references `meta.xr.mrutilitykit` — the Meta
   dependency must become optional (separate Quest asmdef, or a define
   constraint) so an iOS-only project doesn't need the Meta SDK.
5. Same `room_config.json` on both devices → a phone and a headset should place
   room-frame content in the same physical spot; worth testing side by side.

Mac setup: Unity 6000.3.10f1 + iOS Build Support, Xcode, an Apple developer
account (free is fine for your own device). Unity generates the Xcode project;
build/sign/install from Xcode.

## Open items

- README fixes: yaw convention (wrong), 5/9 size rule, tagStandard41h12 called
  out, left-handed frame, `AprilTagRoomLocalizer`/`RoomFit` documented.
  `Samples~/Diagnostics` is empty though the README references it.
- Tape-measure the room width (implied 4.46 m vs 4.61 m configured).
- Original `~/Unity/AprilTag` config: sizes 0.0639 → 0.0944 and yaws need the
  verified convention.
- Push AprilTags2 to GitHub (needs an empty repo created first).
