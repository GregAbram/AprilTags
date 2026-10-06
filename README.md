# TACC AprilTag Colocation

Multi-user AR colocation: wall-mounted AprilTags let independent AR sessions
agree on a shared physical room coordinate system, so content
placed by room coordinates appears in the same physical spot for every user.

Platforms: **Meta Quest 3** and **iPhone** via AR Foundation + ARKit (both
tested; iPhone 11, iOS 26). The core is free of any
platform SDK.

## Prerequisites (manual installs)

Unity's package manager can only auto-resolve dependencies that come from a
registry. These do not, and must be installed manually **before** installing
this package:

1. **[edu.umn.cs.ivlab.apriltag](https://github.com/GregAbram/AprilTag-UnityPackage)**
   (a fork of [ivlab/AprilTag-UnityPackage](https://github.com/ivlab/AprilTag-UnityPackage),
   kept so this dependency can't disappear) — Package Manager → **+** →
   Install package from git URL →
   `https://github.com/GregAbram/AprilTag-UnityPackage.git#e7fb480608798b693191f989413ca03eef189961`
   (pinned to the commit this package was tested with)

For **Meta Quest**, also:

2. **Meta XR Core SDK** — via the Unity Asset Store (requires a Unity account
   sign-in); installing this also registers Meta's scoped registry. Then
   install **Meta MR Utility Kit** (`com.meta.xr.mrutilitykit`). The Quest
   components (`Runtime/Quest`, assembly `TaccAprilTags.Quest`) compile only
   when MRUK is present; without it, only the platform-neutral core is built.
3. Your project must target **Android**, with **OpenXR** enabled under
   XR Plug-in Management, and the Meta Quest feature group / Touch controller
   profile configured. See Meta's own Passthrough Camera API setup docs.

For **iPhone**, also install **AR Foundation** and the **Apple ARKit XR Plugin**
(6.x; tested to compile with 6.6.2), target iOS, and enable ARKit under XR
Plug-in Management. The AR Foundation components (`Runtime/ARFoundation`,
assembly `TaccAprilTags.ARFoundation`) compile only when AR Foundation is
present.

## How it works

1. Each physical AprilTag is measured once and recorded in a JSON config
   (position + facing direction, in a room coordinate system you define).
2. At runtime, `AprilTagRoomLocalizer` detects any tag in that config in the
   camera image, solves for the room origin's pose in the current
   session's (arbitrary) tracking space, and raises an event with the result.
   Content is then placed using room-frame coordinates rather than
   session-relative ones.
3. Because each session solves this independently from whichever tag it
   sees, the same room-frame content lands in the same physical spot
   regardless of which headset or session is running.

## Components

Platform-neutral core (`Runtime/`, assembly `TaccAprilTags.Runtime`):

- **`AprilTagRoomLocalizer`** — loads the room config, runs detection on
  frames from an `AprilTagCameraSource`, and produces one `RoomOriginEstimate` per
  `BeginAcquisition()` call via the `EstimateAcquired` event. An estimate
  averages a 1 s window of continuous sightings of a single tag; the window
  restarts if the tag is unseen for more than 0.25 s. Detection waits 2 s after
  startup for tracking to settle, and ignores detections more than 20° off the
  image center. All of these are inspector fields. It uses the camera source
  assigned in the inspector, else one on the same GameObject, else the first
  in the scene.
- **`AprilTagCameraSource`** — abstract component that delivers grayscale
  frames (row 0 at the top), the camera's world pose at capture time, and
  pinhole intrinsics (`PinholeIntrinsics`) in that frame's pixel space. One
  implementation per platform.
- **`RoomOriginEstimate`** — the room origin's pose in session world space
  from one tag (`RoomToWorld(roomPosition)` maps room coordinates to world),
  plus that tag's averaged detected world position and its configured room
  position.
- **`RoomFit`** — best-fit room pose from **several** tags' detected
  positions, ignoring each tag's own orientation estimate. Gravity fixes "up"
  in both frames, so it solves only for yaw and translation (weighted 2D
  Kabsch/Procrustes in the horizontal plane). Needs at least two tags spread
  ≥ 0.3 m apart horizontally, and returns per-tag residuals in room axes.
  Single-tag yaw is the weakest part of an estimate (1° of yaw error ≈ 4 cm of
  origin error per 2.2 m of tag-to-origin distance), so prefer `RoomFit`
  whenever more than one tag can be scanned. Feed it the `TagWorldPosition` /
  `TagRoomPosition` pairs from several estimates.

Meta Quest (`Runtime/Quest/`, assembly `TaccAprilTags.Quest`):

- **`PassthroughCameraSource`** — camera source for `PassthroughCameraAccess`
  (GPU readback of its texture). Uses the first `PassthroughCameraAccess` in
  the scene if none is assigned.
- **`AprilTagBoxPlacement`** — simple drop-in: places one transform at a fixed
  room pose using the first tag acquired after startup. Uses an
  `AprilTagRoomLocalizer` on the same GameObject, or adds one if absent, and
  adds a `PassthroughCameraSource` if the GameObject has no camera source.
- **`TextureIntrinsics`** — maps Meta's sensor intrinsics onto the delivered
  camera texture (see Calibration notes).

AR Foundation / iPhone (`Runtime/ARFoundation/`, assembly
`TaccAprilTags.ARFoundation`):

- **`ARFoundationCameraSource`** — camera source for `ARCameraManager`: copies
  the Y (luminance) plane of the latest CPU image and uses ARKit's intrinsics
  for it. The CPU image is always in the sensor's landscape orientation, so
  the camera pose is rolled to match the current screen orientation. Has a
  `Flip Vertical` toggle in case tags never detect on a device. Uses the first
  `ARCameraManager` in the scene if none is assigned.

**Upgrading from 0.1:** `AprilTagRoomLocalizer` no longer has a camera field.
In scenes that place a localizer directly, add a `PassthroughCameraSource`
(to the same GameObject, or anywhere in the scene); otherwise the localizer
logs an error and detects nothing. `AprilTagBoxPlacement` scenes need no
change.

## Room config

Provide a JSON file (default name `room_config.json`) either bundled in your
app's `StreamingAssets/` (fallback default) or written to
`Application.persistentDataPath/room_config.json` (checked first — lets you
swap a different room's config onto a device without rebuilding, e.g. by
`adb push` to `/sdcard/Android/data/<app id>/files/room_config.json`; delete it
to revert):

```json
{
  "room": { "width": 4.6, "depth": 5.0, "height": 2.7 },
  "tags": [
    { "id": 8, "x": -1.11, "y": 1.39, "z": -0.015, "yawDegrees": 0,   "sizeMeters": 0.0944 },
    { "id": 7, "x":  0.0,  "y": 1.43, "z": -2.240, "yawDegrees": 270, "sizeMeters": 0.0944 },
    { "id": 9, "x": -4.61, "y": 0.91, "z": -2.460, "yawDegrees": 90,  "sizeMeters": 0.0944 }
  ]
}
```

(This is the TACC test room. Tags 7 and 9 are on the side walls; their yaws
were set empirically — see the `yawDegrees` caveat below.)

### Tag family

Only **`tagStandard41h12`** is detected. Tags from other families — including
the common default `tag36h11` — are silently never found.

### Room coordinate frame

The room frame is **left-handed, like Unity's**: +X right, +Y up (against
gravity), +Z forward. Pick a "front" wall and stand facing it: +X is to your
right and +Z points straight ahead into that wall. The origin can be anywhere
(e.g. on the front wall, or a floor corner).

Measure X and Z with this handedness. Measuring along the walls the other way
round gives a mirrored room, which no rotation can correct.

### Fields

- `id`: the tag's ID in the tagStandard41h12 family.
- `x, y, z`: the tag's **center**, in room coordinates (meters).
- `yawDegrees`: **the direction you face when looking straight at the tag**,
  as a rotation about +Y from +Z (the tag's own +Z points into its wall). A tag
  on the front wall is `0`; with Unity's clockwise-from-above yaw, a tag you
  look at while facing +X is `90`, facing −Z is `180`, facing −X is `270`.
  Only pure yaw is supported — mount tags flat and upright on vertical walls.
  **Verify side-wall tags empirically:** a 180° yaw error shows up as the
  origin mirrored through that tag (content appears roughly twice as far away,
  beyond the tag). In the test room, side-wall tags needed the opposite of the
  value their wall position suggested; the cause hasn't been pinned down.
- `sizeMeters`: the edge length of the tag's **black border square** — *not*
  the full printed tag. In tagStandard41h12 the black border ring spans the
  middle 5 of the tag's 9 cells (the outermost data ring lies outside it), so
  **`sizeMeters` = 5/9 × printed tag width**. E.g. a 17 cm printed tag →
  `0.0944`. Getting this wrong scales all distances (a value of 0.0639 on
  17 cm tags made distances read ~66% of true). Per-tag, so tags of different
  physical sizes are supported.
- `room` (`width`, `depth`, `height`): informational only; unused by the code.

The file must be **strict JSON** — e.g. `00` (a leading zero) is invalid. A
malformed or empty config logs an error and no tags will be recognized.

## Calibration notes

- **Intrinsics:** on Quest 3, `Intrinsics.SensorResolution` reports 1280×1280
  while `GetTexture()` delivers 1280×960. The texture is a **center crop** of
  the sensor image at scale 1 (not a resize), mirroring MRUK's
  `CalcSensorCropRegion`; `TextureIntrinsics` applies that crop and flips the
  principal point's Y (sensor coordinates are bottom-origin, the detector
  image is top-origin). Expected result on device: fx = fy ≈ 866.6,
  cx ≈ 643.7, cy ≈ 478.6. The detector package's own `TagDetector` is not used
  because it assumes a single FOV and a centered principal point; this package
  calls its `Interop` layer directly with explicit fx/fy/cx/cy.
- **Image orientation:** on Quest, frames are flipped vertically (Unity textures are
  bottom-up) and the green channel is used as grayscale. A wrongly oriented
  image makes tags undetectable (mirrored codes don't decode), so if nothing is
  ever detected on new hardware, suspect orientation first.
- **Off-axis limit:** detections beyond `Max Off Axis Angle Degrees`
  (default 20°) are discarded, since accuracy was only validated within that
  range.
- **Distance sanity check:** stand with the headset front exactly 1.00 m from a
  tag; the logged camera-local z should be ≈ 0.98–1.0. If it's off by a
  constant factor, check `sizeMeters`.

## Troubleshooting

- Logs are prefixed `[AprilTagRoomLocalizer]`, `[PassthroughCameraSource]`,
  `[AprilTagBoxPlacement]`:
  `adb logcat | grep "\[AprilTag"`. The lock line includes the tag's
  camera-local position (z = distance), pixel center and intrinsics.
- **Runtime-created objects render pink or differently per eye** (URP,
  single-pass stereo): `GameObject.CreatePrimitive` at runtime gets the
  built-in default material. Assign a URP material that a scene references so
  its shader and stereo variants survive build stripping.
- Detection is skipped while the passthrough camera is paused (e.g. headset
  removed).
