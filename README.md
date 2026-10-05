# TACC AprilTag Colocation

Multi-user AR colocation for Meta Quest: wall-mounted AprilTags let independent
headset sessions agree on a shared physical room coordinate system, so content
placed by room coordinates appears in the same physical spot for every user.

## Prerequisites (manual installs)

Unity's package manager can only auto-resolve dependencies that come from a
registry. These do not, and must be installed manually **before** installing
this package:

1. **[edu.umn.cs.ivlab.apriltag](https://github.com/GregAbram/AprilTag-UnityPackage)**
   (a fork of [ivlab/AprilTag-UnityPackage](https://github.com/ivlab/AprilTag-UnityPackage),
   kept so this dependency can't disappear) — Package Manager → **+** →
   Install package from git URL →
   `https://github.com/GregAbram/AprilTag-UnityPackage.git#82a5ae5ed072011fe0bf47f80ebdb92efad4a6fd`
   (pinned to the commit this package was tested with)
2. **Meta XR Core SDK** — via the Unity Asset Store (requires a Unity account
   sign-in); installing this also registers Meta's scoped registry, which lets
   `com.meta.xr.mrutilitykit` (a listed dependency here) resolve normally
   afterward.
3. Your project must target **Android**, with **OpenXR** enabled under
   XR Plug-in Management, and the Meta Quest feature group / Touch controller
   profile configured. See Meta's own Passthrough Camera API setup docs.

## How it works

1. Each physical AprilTag is measured once and recorded in a JSON config
   (position + facing direction, in room coordinates you define — e.g. origin
   at a floor corner, X/Z along the two walls meeting there, Y up).
2. At runtime, `AprilTagBoxPlacement` detects any tag in that config, solves
   for the room origin's pose in the current session's (arbitrary) tracking
   space, and places content using room-frame coordinates rather than
   session-relative ones.
3. Because each session solves this independently from whichever tag it
   sees, the same room-frame content lands in the same physical spot
   regardless of which headset or session is running.

## Room config schema

Provide a JSON file (default name `room_config.json`) either bundled in your
app's `StreamingAssets/` (fallback default) or written to
`Application.persistentDataPath/room_config.json` (checked first — lets you
swap a different room's config onto a device via `adb push` without
rebuilding):

```json
{
  "room": { "width": 6.0, "depth": 4.5, "height": 2.7 },
  "tags": [
    { "id": 10, "x": 0.0, "y": 0.0, "z": 0.0, "yawDegrees": 0, "sizeMeters": 0.0639 }
  ]
}
```

- `x, y, z`: the tag's **center**, in room coordinates.
- `yawDegrees`: rotation of the tag's outward-facing normal around the room's
  vertical (Y) axis. `0` is whatever direction you choose as your reference
  (e.g. the first tag you calibrate against); `90`/`180`/`270` follow from
  there. Only pure yaw is supported — mount tags flush and upright, not
  tilted, since orientation is inferred from which wall a tag is on rather
  than measured directly.
- `sizeMeters`: the physical edge length of the tag's **solid black border**
  (not the white quiet-zone margin, not the inner bit pattern) — measure with
  a ruler. Per-tag, so tags of different physical sizes are supported.

## Calibration notes

The focal length/principal point handling in `AprilTagBoxPlacement` includes
corrections for two real, non-obvious issues found during development on
Quest 3:

- `Intrinsics.SensorResolution`/`PrincipalPoint` can describe a different
  (larger) resolution than the texture actually delivered by
  `GetTexture()` — the principal point is rescaled onto the delivered
  resolution at startup.
- Detections beyond `Max Off Axis Angle Degrees` (default 20°) are discarded
  rather than used, since accuracy was only validated within that range via
  ground-truth testing (see `Samples~/Diagnostics/AprilTagGroundTruthTest.cs`
  for the methodology, useful if recalibrating for different hardware).

## Samples

Import **Diagnostics** via Package Manager for the tools used to validate and
recalibrate this system: a live camera feed viewer, a raw detection logger,
and the ground-truth alignment tester (floats a reference marker at a known
distance/angle for you to align by eye against a physical tag, to measure
real position/angle error rather than guessing).
