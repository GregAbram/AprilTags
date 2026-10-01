using System.Collections.Generic;
using UnityEngine;

// One tag's configured room-frame position paired with where it was detected
// in this session's world space.
public readonly struct TagCorrespondence
{
    public readonly int TagId;
    public readonly Vector3 RoomPosition;
    public readonly Vector3 WorldPosition;
    public readonly float Weight;

    public TagCorrespondence(int tagId, Vector3 roomPosition, Vector3 worldPosition, float weight = 1f)
    {
        TagId = tagId;
        RoomPosition = roomPosition;
        WorldPosition = worldPosition;
        Weight = weight;
    }
}

public readonly struct RoomFitResult
{
    // Room origin pose in world space: world = Position + Rotation * room.
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;

    // Per tag: detected minus fitted position, in room axes (x, y, z as in room_config).
    public readonly IReadOnlyList<(int tagId, Vector3 residual)> Residuals;
    public readonly float RmsResidual;

    public RoomFitResult(Vector3 position, Quaternion rotation, IReadOnlyList<(int, Vector3)> residuals, float rmsResidual)
    {
        Position = position;
        Rotation = rotation;
        Residuals = residuals;
        RmsResidual = rmsResidual;
    }

    public Vector3 RoomToWorld(Vector3 roomPosition) => Position + Rotation * roomPosition;
}

// Best-fit room pose from several tags' positions alone, ignoring each tag's own
// (much noisier) orientation estimate. Gravity fixes "up" in both frames, so the
// unknowns are a yaw and a translation: a weighted 2D Procrustes/Kabsch problem
// in the horizontal plane, with the vertical offset taken from the centroids.
public static class RoomFit
{
    // Below this horizontal spread between tags the yaw is poorly determined.
    public const float MinHorizontalSpreadMeters = 0.3f;

    public static bool TryFit(IReadOnlyList<TagCorrespondence> tags, out RoomFitResult result)
    {
        result = default;
        if (tags == null || tags.Count < 2)
        {
            return false;
        }

        var weightSum = 0f;
        var roomCentroid = Vector3.zero;
        var worldCentroid = Vector3.zero;
        foreach (var tag in tags)
        {
            weightSum += tag.Weight;
            roomCentroid += tag.Weight * tag.RoomPosition;
            worldCentroid += tag.Weight * tag.WorldPosition;
        }
        if (weightSum <= 0f)
        {
            return false;
        }
        roomCentroid /= weightSum;
        worldCentroid /= weightSum;

        // Unity yaw R(θ) maps (x, z) to (x cosθ + z sinθ, -x sinθ + z cosθ).
        // Maximizing Σ w b·R(θ)a over centered pairs a (room), b (world) gives
        // θ = atan2(Σ w (bx az - bz ax), Σ w (bx ax + bz az)).
        var sinTerm = 0f;
        var cosTerm = 0f;
        var maxSpread = 0f;
        foreach (var tag in tags)
        {
            var a = tag.RoomPosition - roomCentroid;
            var b = tag.WorldPosition - worldCentroid;
            sinTerm += tag.Weight * (b.x * a.z - b.z * a.x);
            cosTerm += tag.Weight * (b.x * a.x + b.z * a.z);
            maxSpread = Mathf.Max(maxSpread, new Vector2(a.x, a.z).magnitude);
        }
        if (maxSpread < MinHorizontalSpreadMeters)
        {
            return false;
        }

        var yawDegrees = Mathf.Atan2(sinTerm, cosTerm) * Mathf.Rad2Deg;
        var rotation = Quaternion.Euler(0f, yawDegrees, 0f);
        var position = worldCentroid - rotation * roomCentroid;

        var residuals = new List<(int, Vector3)>(tags.Count);
        var squaredSum = 0f;
        var inverseRotation = Quaternion.Inverse(rotation);
        foreach (var tag in tags)
        {
            var residualWorld = tag.WorldPosition - (position + rotation * tag.RoomPosition);
            residuals.Add((tag.TagId, inverseRotation * residualWorld));
            squaredSum += residualWorld.sqrMagnitude;
        }

        result = new RoomFitResult(position, rotation, residuals, Mathf.Sqrt(squaredSum / tags.Count));
        return true;
    }
}
