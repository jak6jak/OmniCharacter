using System;
using Godot;

namespace CharacterKit.Abilities;

/// Forgiving aimed targeting. Precise ray first, then a small cone sweep.
///
/// Framework-shaped because every aimed ability wants it — grapple, blink,
/// ledge grab, interact prompts. What counts as a *valid* target is not
/// framework-shaped, so that stays a caller-supplied predicate.
public sealed class AimAssist
{
    public float MaxRange = 30f;
    public float ConeDegrees = 4f;

    /// Gameplay policy. Return the node to track (for moving or destructible
    /// targets) or null to accept static world geometry.
    public required Func<Node3D, (bool Valid, Node3D Track)> Accept;

    private static readonly Vector2[] ConeOffsets =
    {
        new(0, 1), new(0, -1), new(1, 0), new(-1, 0),
        new(0.7f, 0.7f), new(-0.7f, 0.7f), new(0.7f, -0.7f), new(-0.7f, -0.7f)
    };

    public bool Find(Camera3D cam, Rid exclude, out Vector3 point, out Node3D track)
    {
        var origin = cam.GlobalPosition;
        var basis = cam.GlobalTransform.Basis;

        if (Cast(cam, origin, -basis.Z, exclude, out point, out track)) return true;

        float half = Mathf.DegToRad(ConeDegrees);
        float best = float.MaxValue;
        Vector3 bestPoint = default;
        Node3D bestTrack = null;
        bool found = false;

        foreach (var o in ConeOffsets)
        {
            var dir = (-basis.Z + basis.X * (o.X * half) + basis.Y * (o.Y * half)).Normalized();
            if (!Cast(cam, origin, dir, exclude, out var p, out var t)) continue;

            float d = origin.DistanceSquaredTo(p);
            if (d >= best) continue;

            best = d; bestPoint = p; bestTrack = t; found = true;
        }

        point = bestPoint;
        track = bestTrack;
        return found;
    }

    private bool Cast(Node3D ctx, Vector3 origin, Vector3 dir, Rid exclude,
                      out Vector3 point, out Node3D track)
    {
        point = default;
        track = null;

        var query = PhysicsRayQueryParameters3D.Create(origin, origin + dir * MaxRange);
        query.Exclude = new Godot.Collections.Array<Rid> { exclude };

        var hit = ctx.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return false;

        var collider = hit["collider"].As<Node3D>();
        var (valid, tracked) = Accept(collider);
        if (!valid) return false;

        point = hit["position"].AsVector3();
        track = tracked;
        return true;
    }
}
