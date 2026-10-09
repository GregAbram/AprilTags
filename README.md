# TACC AprilTag Colocation

Multi-user AR colocation: wall-mounted AprilTags let independent AR sessions on
different devices agree on one room coordinate system, so content placed in
room coordinates appears in the same physical spot for every viewer.

Platforms: **Meta Quest 3** (Meta XR SDK + MR Utility Kit) and **iPhone**
(AR Foundation + ARKit), both tested (Quest 3, iPhone 11 / iOS 26). The core is
free of platform SDKs. Used by [SharedAR](https://github.com/GregAbram/SharedAR).

What matters is that **viewers agree with each other**; where exactly the room
frame sits relative to the real walls is secondary. Everything below is built
around that.

## How it works

- Tags are **detected continuously** at a low rate on a worker thread (no
  button), and every sighting of a known tag is a single-frame observation.
- **`RoomAnchor`** is a Transform that moves to where the room is: put your
  content under it, in room coordinates. It is *anchored* by **fitting two or
  more tags' positions** (`RoomFit`; tag orientations are not used), so it
  needs two tags in view at some point; after that one tag keeps it in place.
  Content stays hidden until then.
- Tag positions come from **`room_config.json`**. Rather than tape-measuring
  every tag, **survey** the room once: anchor on one tag, let the others be
  learned relative to it, save the result, and give that one file to every
  device. Devices then share identical tag positions, so they agree.

Why positions and not single tags: a tag's *position* is measured to ~1 cm per
frame even at 2.5 m, but its *orientation* (the room's yaw from one tag) is only
good to ~0.3° up close on Quest, 1–3° on iPhone or at a distance - and 1° of yaw
is ~7 cm at 4 m. See [Documentation~/BackgroundScan.md](Documentation~/BackgroundScan.md)
for the measurements.

## Setting up a room

1. **Print tags** from the **tagStandard41h12** family (other families,
   including the common tag36h11, are never detected), all the same size if
   you can. Note `sizeMeters` = **5/9 × the printed tag width** (the black border
   square; see the config reference).
2. **Hang them** flat and upright on the walls, spread around the room, so
   that from anywhere a viewer stands at least two are in view. Tags lying flat
   (on a table) are ignored by the survey.
3. **Write a minimal `room_config.json`** with the size and *one* tag, roughly
   measured - it just defines where the room frame is:
   ```json
   {
     "learnUnlistedTags": true,
     "defaultTagSizeMeters": 0.0944,
     "tags": [ { "id": 8, "x": 0, "y": 1.4, "z": 0, "yawDegrees": 0, "sizeMeters": 0.0944, "measured": true } ]
   }
   ```
   Or list no tags at all: the first tag the survey sees then defines the room
   (its center is the origin, its facing sets the axes).
4. **Survey with a Quest 3** (more consistent than a phone; see accuracy notes):
   in a scene with a `RoomAnchor` in **survey mode**, walk within ~1 m of the
   configured tag until anchored, then look at every other tag until each
   shows *learned* (positions are weighted toward close sightings, so get
   within ~2 m). Save (`RoomAnchor.SaveSurveyedConfig()`; the
   `SurveyScreenUI` and SharedAR's Quest survey scene do this). The new config
   lists every tag as measured and is written to `persistentDataPath`, where it
   overrides the bundled config on that device from the next start.
5. **Distribute that file** to every device: put it in the app's
   `StreamingAssets/room_config.json` and rebuild, or copy it into each
   device's `persistentDataPath` (`adb push`, `devicectl device copy to`).
   Re-survey if a tag moves.

## Using it in an app

1. **Install** (Unity can only auto-resolve registry dependencies, so add both
   git packages yourself, pinned):
   ```json
   "edu.umn.cs.ivlab.apriltag": "https://github.com/GregAbram/AprilTag-UnityPackage.git#e7fb480608798b693191f989413ca03eef189961",
   "edu.tacc.apriltags": "https://github.com/GregAbram/AprilTags.git#<commit>"
   ```
2. **Platform setup**
   - **iPhone:** AR Foundation + Apple ARKit XR Plugin (6.x), ARKit enabled in
     XR Plug-in Management, a camera usage description. Scene: AR Session + XR
     Origin (Mobile AR). Camera source: `ARFoundationCameraSource`.
   - **Quest 3:** Meta XR Core SDK + MR Utility Kit (**207 or later** if the
     project also builds for iOS - 203 breaks iOS player builds), Android
     target, OpenXR with Meta's XR feature, and
     `<uses-permission android:name="horizonos.permission.HEADSET_CAMERA" />` in
     the Android manifest. Scene: **`OVRCameraRig`** (passthrough enabled,
     camera permission requested at startup) and a `PassthroughCameraAccess`.
     Use OVRCameraRig, not an XR Origin: MRUK reports camera poses in Meta's
     tracking space, which is Unity world space only under OVRCameraRig.
     Camera source: `PassthroughCameraSource`.
   - With the Meta SDK in a project, **every editor needs Android Build
     Support installed** (Mac included) - its editor code doesn't compile
     without it.
3. **Scene:** the camera source and an **`AprilTagRoomLocalizer`** (one object
   is fine), and a root-level **`RoomAnchor`** (unit scale) referencing the
   localizer. **Make your content children of the RoomAnchor**, positioned in
   room coordinates. Optional: `RoomTagMarkers` (cubes on placed tags),
   `RoomReferenceMarker` (a post at a fixed room point, for checking that
   viewers agree), `RoomScreenUI` / `SurveyScreenUI` (on-screen controls for
   phones and the editor).
4. **Config:** `StreamingAssets/room_config.json` (see above).

## Room config reference

```json
{
  "room": { "width": 4.61, "depth": 5.0, "height": 2.7 },
  "learnUnlistedTags": true,
  "defaultTagSizeMeters": 0.0944,
  "tags": [
    { "id": 8, "x": -1.110, "y": 1.390, "z": -0.015, "yawDegrees": 0.0,   "sizeMeters": 0.0944, "measured": true },
    { "id": 7, "x":  0.086, "y": 1.427, "z": -2.308, "yawDegrees": 89.7,  "sizeMeters": 0.0944, "measured": true }
  ]
}
```

- **`tags[]`**
  - `id`: the tag's ID in tagStandard41h12.
  - `x, y, z`: the tag's **center** in room coordinates (meters).
  - `yawDegrees`: the direction you face when looking straight at the tag, as
    a rotation about +Y from +Z (a tag on the front wall is 0; facing +X, 90).
    Only used when a single tag places the room. **Let the survey learn yaws**
    rather than entering them: hand-entered side-wall yaws have been wrong by
    180° before.
  - `sizeMeters`: edge length of the tag's **black border square** = 5/9 × the
    printed width (the outermost data ring lies outside the border). A wrong
    value scales every distance.
  - `measured`: the tag's pose is trusted and places the room. If no tag is
    marked, all are treated as measured (older configs).
- **`learnUnlistedTags`** with **`defaultTagSizeMeters`**: also detect tags not
  in the list (perfect decodes only) and learn their positions relative to the
  measured tags at runtime. A tag printed at another size must be listed.
- `room`: informational only.

**Room frame:** left-handed like Unity: +X right, +Y up, +Z forward. Stand
facing a "front" wall: +X is to your right, +Z points into that wall. Measure
with this handedness - the other way round gives a mirrored room.

The file must be strict JSON. A config in `persistentDataPath` (same name)
overrides the bundled one; delete it to revert.

## Components

Core (`Runtime/`, assembly `TaccAprilTags.Runtime`):

- **`AprilTagRoomLocalizer`** - loads the config and detects tags in frames from
  an `AprilTagCameraSource`. Detection and per-tag pose run on a worker thread.
  - Continuous scanning (`ContinuousScan`, `ScanIntervalSeconds`): every
    sighting of a known tag raises **`TagObserved(TagObservation)`** (world
    position and orientation, distance, camera position, rotation speed).
  - Frames taken while the camera turns faster than 8°/s are dropped.
  - Uses each frame's own intrinsics when the source provides them.
  - Logs a timing summary every 30 s.
  - Acquisition API: `BeginAcquisition()` → `EstimateAcquired(RoomOriginEstimate)`,
    one estimate averaged over a 1 s window of one tag (used by SharedAR's
    AprilTags demo scenes).
- **`RoomAnchor`** - the room frame (see *How it works*).
  - Keeps per-tag observation histories, which age out after 60 s for drift.
  - Anchors by `RoomFit` on two or more known tags. One close tag anchors
    only when the config has fewer than two measured tags, or in a survey.
  - Learns unmeasured and unlisted tags (position and yaw), saved to
    `learned_tags.json`, keyed to the measured tags' survey.
  - Detects tracking resets.
  - Adapts the scan rate: fast while a visible tag needs samples, 3 s once anchored.
  - Smooths the transform.
  - Status: `StatusText`, `IsAnchored`, `LastFit`.
  - Survey: `surveyMode`, `BuildSurveyedConfig()`, `SaveSurveyedConfig()`.
- **`RoomFit`** - best-fit yaw and translation from several tags' positions
  (weighted 2D Kabsch in the horizontal plane; gravity fixes up). Needs tags
  ≥ 0.3 m apart horizontally. Returns per-tag residuals.
- **`RoomConfig`**, **`AprilTagCameraSource`** (frames, camera pose at capture,
  optional per-frame intrinsics), **`PinholeIntrinsics`**.
- UI (`Runtime/UI`): **`RoomScreenUI`**, **`SurveyScreenUI`** - OnGUI controls
  and status for phones and the editor.
- Diagnostics (`Runtime/Diagnostics`): **`RoomTagMarkers`** (red = measured,
  yellow while provisional, green = learned), **`RoomReferenceMarker`** (floor
  point below the center of the measured tags), **`SessionLogFile`**
  (opt-in: `SessionLogFile.Begin("app.log")` keeps the whole session's
  `[Component]` log lines in `persistentDataPath`).

iPhone (`Runtime/ARFoundation`, compiles when AR Foundation is present):
**`ARFoundationCameraSource`** - the Y plane of ARKit's CPU image with ARKit's
per-frame intrinsics, camera roll matched to the screen orientation, and
**autofocus off** (focus moves the focal length up to 4%, biasing distances and
orientations).

Quest (`Runtime/Quest`, compiles when MRUK is present):
**`PassthroughCameraSource`** (async GPU readback of `PassthroughCameraAccess`),
`TextureIntrinsics`, and the legacy drop-in `AprilTagBoxPlacement`.

## Accuracy notes

Measured in a 4.6 × 5 m room with 9.4 cm tags (details in
[BackgroundScan.md](Documentation~/BackgroundScan.md)):

- **Positions:** ~1 cm per frame at 2.5 m on both devices. Two- to four-tag
  fits have residuals of 1–4 cm.
- **Single-tag yaw:** about 0.2° repeatable on Quest 3 from similar viewpoints,
  and up to ~1.5° between different viewpoints. On iPhone, 1–2.6° between
  viewpoints even up close: square-on views are noisy, oblique ones steady but
  biased. The likely cause is lens distortion, which AR Foundation doesn't
  expose, so this pinhole model can't correct for it. That's why anchoring
  fits positions, and why surveys are best done on a Quest.
- **Cost:** detection takes 25–40 ms per frame on Quest 3 and 40–110 ms on
  iPhone 11, on the worker thread. The main thread spends 0.2–3 ms per scanned
  frame. Once anchored, a frame every 3 s.
- A survey's single-tag anchoring may leave the room frame turned by about a
  degree relative to the real walls. Viewers still agree, because they share
  the file.

## Calibration notes

- **Quest intrinsics:** `Intrinsics.SensorResolution` reports 1280×1280, while
  `GetTexture()` delivers 1280×960. The texture is a **center crop** at scale 1,
  mirroring MRUK's `CalcSensorCropRegion`. `TextureIntrinsics` applies that crop
  and flips the principal point's Y. Expect fx = fy ≈ 866.5, cx ≈ 643.7,
  cy ≈ 478.8. The detector package's own `TagDetector` assumes a single FOV and
  a centered principal point, so it isn't used; this package calls its
  `Interop` layer directly.
- **Image orientation:** Quest frames are flipped vertically, and the green
  channel serves as grayscale. A mirrored image never decodes, so if nothing
  is ever detected on new hardware, suspect orientation (`Flip Vertical` on the
  iPhone source).
- **Off-axis limit:** detections more than 20° from the image center are
  discarded.
- **Distance check:** at exactly 1.00 m from a tag, the logged camera-local z
  should be about 0.98–1.0. If it's off by a constant factor, check
  `sizeMeters`.

## Troubleshooting

- Log lines are prefixed `[AprilTagRoomLocalizer]`, `[RoomAnchor]`,
  `[ARFoundationCameraSource]` or `[PassthroughCameraSource]`. A device's
  system log is overwritten within minutes, so use `SessionLogFile` and fetch
  the file:
  - Quest: `adb pull /sdcard/Android/data/<app id>/files/<name>.log`
  - iPhone: `xcrun devicectl device copy from --device <id> --domain-type
    appDataContainer --domain-identifier <bundle id> --source Documents/<name>.log
    --destination .`
- **Never anchors:** the status line says what's needed, either *"get a second
  tag in view"* or *"get within 1.2 m of tag N"*. If tags never appear at all,
  check the tag family and `sizeMeters`.
- **Content jumps when re-anchoring, or devices disagree:** fit residuals over
  about 5 cm mean the survey no longer matches the room, for example because
  a tag moved. Re-survey.
- **iPhone install fails** with "tunnel connection failed" or the device shows
  as unavailable: restart the phone. A VPN on the Mac can also cause it.
- **Runtime-created objects render pink** (URP): give them a URP material
  that a scene references, so the shader survives build stripping.

## Upgrading from 0.2

- `RoomAnchor` is new and is the recommended way to place content.
  `EstimateAcquired` and `RoomFit` still work as before.
- The config gains `measured`, `learnUnlistedTags` and `defaultTagSizeMeters`.
  Old configs behave as before: every tag counts as measured.
- `TagObservation` and `CameraFrame` have new fields, and
  `AprilTagCameraSource.LastFramePrepareMilliseconds` is new.
