# Background scanning: measurements and design

Goal: no Acquire button. The device keeps looking for tags at a low rate,
places the room as soon as it sees one configured tag, and keeps refining
(and correcting drift and tracking resets) as it sees more.

## Measurements (Quest 3, 2026-10-08)

Per-frame cost while scanning, from `AprilTagRoomLocalizer`'s timing log
(passthrough camera texture 1280×960, `quadDecimate` 2, tagStandard41h12):

| Per frame | Mean | Max |
|---|---|---|
| prepare: GPU readback copy + grayscale (main thread) | 2.3 ms | 3.0 ms |
| detect (main thread) | 25–29 ms | 38 ms |
| process: pose for known tags | ~1 ms with a tag, ~0.1 ms without | 1.7 ms |
| latency, request → delivery (async readback) | 36 ms | 44 ms |

Detection costs the same with or without a tag in view (the quad search over
the whole image dominates). At 72–90 Hz the frame budget is 11–14 ms, so one
detection on the main thread costs 2–3 display frames: even a 1 Hz scan on the
main thread would judder. **Detection must run off the main thread.**

Per-frame spread within one 1 s acquisition window (16–18 frames), from the
localizer's spread log:

| Tag, distance | Tag position sd / max | Single-tag yaw sd / max | Room origin sd / max |
|---|---|---|---|
| 8, 0.8 m | 0.05 / 0.14 cm | 0.29° / 0.62° | 0.5 / 1.1 cm |
| 8, 2.4 m | 0.9 / 2.1 cm | 3.70° / 6.97° | 6.7 / 12.9 cm |
| 8, 2.4 m | 0.9 / 2.1 cm | 1.13° / 2.04° | 1.7 / 3.0 cm |
| 7, 2.1 m | 0.6 / 1.3 cm | 3.37° / 8.58° | 13.4 / 33.7 cm |
| 9, 2.6 m | 1.0 / 2.3 cm | 2.44° / 4.34° | 21.4 / 37.4 cm |

- A single frame **locates** a tag to ~1 cm (2 cm worst) even at 2.5 m.
- A single frame's **orientation** is poor beyond ~1 m (1–4° sd, up to 9°).
- Room-origin error from one tag is its yaw error × the tag's distance from
  the origin (1.8–5 m here): up to 37 cm per frame, and the 1 s *averages*
  still disagreed by ~3° (10–15 cm across the room) between tags.
- RoomFit over the three tags (positions only, yaw from the tags' relative
  placement) gave RMS 3.6 cm, at the level of the tape-measured config.

## Design

1. **Detection on a worker thread.** The main thread only requests frames and
   applies results; detection and per-tag pose estimation run on a thread-pool
   task. One frame in flight at a time; the source's image is not rewritten
   until the next request, so no copy is needed.
2. **Continuous scan at a low rate** (`AprilTagRoomLocalizer.ContinuousScan`,
   `ScanIntervalSeconds`). Every frame's known-tag detections are reported as
   single-frame observations (`TagObserved`). The manual Acquire window
   (`BeginAcquisition` / `EstimateAcquired`) still works unchanged.
3. **`RoomAnchor` builds the room from observations:**
   - per tag, a short history of world positions (recent ones only, so drift
     ages out), averaged;
   - **two or more tags: RoomFit on the averaged positions**, ignoring tag
     orientations - the accurate mode, and the one that makes devices agree;
   - **one tag: provisional** placement from that tag's own yaw, weighted
     toward close-range observations, until a second tag is seen;
   - **outliers and resets:** a single observation far from its tag's average
     is dropped; several in a row mean the tracking frame moved (recenter,
     relocalization) and the history is restarted;
   - **rate control:** scan every frame briefly while a visible tag still
     needs samples, then at the normal interval, and slower once the fit is
     stable;
   - the displayed transform eases toward the new pose instead of jumping
     (snaps on first placement and after a reset).

## Measured anchors and learned tags (added 2026-10-08)

The first Quest test placed tag 9's marker visibly in front of its wall: with
every configured tag treated as ground truth, a rough config entry pulls the
fit and is drawn where the config says, not where the tag is. And if devices
anchor on different tags they disagree by those config errors. So:

- `"measured": true` marks the carefully surveyed tags (if none is marked, all
  are). **Only measured tags place the room.** It is *anchored* by two or more
  measured tags (RoomFit), or by one measured tag seen within 1.2 m in at least
  8 frames, with yaw from those close frames only (0.3° sd per frame at 0.8 m,
  so well under 1 cm at 5 m). Before that the placement is *provisional* and
  nothing is learned.
- Every other tag's room position is **learned** once anchored: each sighting
  is mapped into room coordinates and averaged (weighted toward close range).
  Saved to `learned_tags.json` in persistentDataPath, keyed by a fingerprint of
  the measured tags' surveyed poses so a re-survey discards stale learning.
- `"learnUnlistedTags": true` with `"defaultTagSizeMeters"` also detects tags
  not in the config at all (perfect decodes only); a tag printed at another
  size must be listed with its size, since distance scales with it.
- Established learned tags (20+ sightings) help hold the room against drift at
  half the weight of measured ones and can re-anchor after a tracking reset.
  With one tag in view only its position corrects the room, never its yaw.
- Only measured or established tags can declare a tracking reset; a learned
  tag that keeps disagreeing (moved, or hand-held) is unlearned instead.

## Two-device findings (2026-10-08/09)

- iPhone 11 timings: detect 40–110 ms per frame (1920×1440) on the worker,
  main thread 0.2–0.5 ms per scanned frame.
- Each device learning tags 7 and 9 independently from one anchor tag: Quest
  repeated to 1.3–1.7 cm over three runs; iPhone spread 20–29 cm. Not motion
  (rotation 0–7 deg/s, frames over 8 deg/s dropped); the iPhone's single-tag
  yaw varies 1–2.6 deg between viewpoints while steady within each
  (0.1–0.3 deg per frame). Autofocus moved its focal length up to 4%
  (now off, per-frame intrinsics): 3.9 deg spread became 2.6. The rest is
  most likely uncorrected lens distortion. Quest too varied ~1.5 deg between
  distant viewpoints.
- Hence: anchor only by fitting two or more tags' positions when the config
  has them, survey once (on Quest), share the file. Quest and iPhone then
  agreed on a reference post at the room center to a few cm.

## Open questions

- Coarser decimation for idle scans: 4 would cut detection ~4× but may lose
  tags at 2–3 m (a 9.4 cm tag is only ~20 px there).
- Drift rate on Quest and iPhone over minutes, to set the history age.
- Correcting the iPhone's lens distortion (ARKit's per-device calibration via a
  native plugin, or a one-time checkerboard calibration) would make
  single-tag yaw, and iPhone surveys, more trustworthy.
