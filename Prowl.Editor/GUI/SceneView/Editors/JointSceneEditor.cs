// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Editor.Core;
using Prowl.Editor.Theming;
using Prowl.PaperUI;
using Prowl.Runtime;
using Prowl.Vector;

using GizmoUtils = Prowl.OrigamiUI.Gizmo.GizmoUtils;
using SColor = System.Drawing.Color;

namespace Prowl.Editor.GUI.SceneView.Editors;

/// <summary>
/// Scene-view handles for the 2D joints, so a joint can be set up by dragging instead of typing numbers. Available whenever the
/// selected GameObject has a <see cref="Joint2D"/>; one tool serves every joint type on it.
/// <list type="bullet">
/// <item><b>Anchor</b> (cyan circle): where the joint attaches to this body. With <see cref="Joint2D.AutoConfigureConnectedAnchor"/> on
/// this is the only anchor, and the connected side follows it.</item>
/// <item><b>Connected anchor</b> (white square): where it attaches to the connected body, or in the world. Only shown, and only
/// draggable, once Auto Configure is off, so the two anchors can never sit under each other and fight for the mouse.</item>
/// <item><b>Axis</b> (green diamond): the slide direction of a prismatic or wheel joint.</item>
/// <item><b>Limits</b> (yellow diamonds): the angle limits of a revolute joint, the travel limits of a prismatic or wheel joint, and
/// the minimum and maximum of a distance joint, each shown while its limit is switched on.</item>
/// <item><b>Rest distance</b> (orange dot): the length of a distance joint whose distance is not measured automatically.</item>
/// </list>
/// Edits go through the components' public properties, so they behave exactly as typing the value would (a hinge being dragged while
/// the game runs is rebuilt live) and are recorded for undo. The tool-strip button hides the handles.
/// </summary>
[ComponentSceneTool(typeof(Joint2D))]
public sealed class JointSceneEditor : SceneTool
{
    public override string Name => "Joint Handles";
    public override string Icon => EditorIcons.Link;
    public override string? Tooltip => "Show draggable handles for the selected joints: anchors, axis and limits";

    /// <summary>Screen-space grab radius of a handle, in pixels.</summary>
    private const float GrabRadius = 9f;

    /// <summary>How far from the anchor the axis handle and the revolute limit handles sit, in world units.</summary>
    private const float AxisLength = 1.25f;
    private const float LimitRadius = 0.8f;

    private const float MinDistance = 0.005f;

    private static readonly Color32 AnchorColor = new(80, 220, 255, 255);
    private static readonly Color32 ConnectedColor = new(255, 255, 255, 255);
    private static readonly Color32 AxisColor = new(90, 220, 130, 255);
    private static readonly Color32 LimitColor = new(255, 215, 60, 255);
    private static readonly Color32 RestColor = new(255, 150, 60, 255);

    private enum Kind { Anchor, ConnectedAnchor, Axis, LowerLimit, UpperLimit, RestDistance }

    private struct HandleInfo
    {
        public Joint2D Joint;
        public Kind Kind;
        public ControlID Id;
        public Float3 Position;
    }

    /// <summary>The drag in progress in one viewport. Per viewport, so two scene views never share a grab.</summary>
    private sealed class DragState
    {
        public ControlID Control = ControlID.None;
        public Joint2D? Joint;
        public Kind Kind;
        public float PlaneZ;
        public Float3 GrabOffset;   // handle position minus where the mouse hit the plane, so the handle does not jump to the cursor
        public Float3 LastPosition; // for the value label
    }

    // Editor-wide, not per viewport: the tool strip is drawn outside the scoped callbacks, so it cannot reach State<T>.
    private bool _showHandles = true;

    private readonly List<HandleInfo> _handles = new();

    // ---- input ---------------------------------------------------------------------------

    public override void OnSceneInput(SceneToolContext toolCtx)
    {
        HandleContext h = toolCtx.Handles;
        DragState drag = toolCtx.State<DragState>();

        GameObject? go = toolCtx.ActiveObject;
        if (!_showHandles || !go.IsValid())
        {
            CancelDrag(h, drag);
            return;
        }

        // Positions are recomputed from the joints every frame, so a handle follows the value it edits. Control ids depend only
        // on the joint's place on the GameObject and the kind of handle, so they stay put for the length of a drag.
        CollectHandles(go);
        if (_handles.Count == 0)
        {
            CancelDrag(h, drag);
            return;
        }

        // Register every handle so they arbitrate against each other and the rest of the viewport. Ties go to the last
        // registration, which is why limits are collected after the axis they may sit on.
        int nearest = -1;
        for (int i = 0; i < _handles.Count; i++)
        {
            HandleInfo info = _handles[i];
            h.AddControl(info.Id, info.Position, GrabRadius);
            h.RequestCursor(info.Id, PaperCursor.Grab);
            if (h.IsNearest(info.Id)) nearest = i;
        }

        if (!drag.Control.IsValid)
        {
            if (nearest >= 0 && h.TryBeginDrag(_handles[nearest].Id))
                BeginDrag(h, drag, _handles[nearest]);
        }
        else
        {
            ContinueDrag(h, drag);
        }

        DrawHandles(h, drag);
    }

    private void BeginDrag(HandleContext h, DragState drag, HandleInfo info)
    {
        drag.Control = info.Id;
        drag.Joint = info.Joint;
        drag.Kind = info.Kind;
        drag.PlaneZ = info.Position.Z;
        drag.LastPosition = info.Position;
        drag.GrabOffset = MousePlaneHit(h, drag.PlaneZ, out Float3 hit) ? info.Position - hit : Float3.Zero;
    }

    private void ContinueDrag(HandleContext h, DragState drag)
    {
        Joint2D? joint = drag.Joint;
        if (joint == null || !joint.IsValid() || !joint.AttachedRigidbody.IsValid() || !h.IsHot(drag.Control))
        {
            CancelDrag(h, drag);
            return;
        }

        if (MousePlaneHit(h, drag.PlaneZ, out Float3 hit))
        {
            Float3 target = hit + drag.GrabOffset;
            // The snapshot has to be taken BEFORE the change: it is what the undo step restores.
            Undo.Snapshot(joint);
            Apply(joint, drag.Kind, target);
            drag.LastPosition = target;
            EditorSceneManager.MarkDirty();
        }

        if (h.TryEndDrag(drag.Control))
            Release(drag);
    }

    private static void CancelDrag(HandleContext h, DragState drag)
    {
        if (drag.Control.IsValid && h.IsHot(drag.Control)) h.TryEndDrag(drag.Control);
        Release(drag);
    }

    private static void Release(DragState drag)
    {
        drag.Control = ControlID.None;
        drag.Joint = null;
    }

    /// <summary>Where the mouse ray meets the plane of a 2D joint (Z = <paramref name="z"/>).</summary>
    private static bool MousePlaneHit(HandleContext h, float z, out Float3 hit)
    {
        var ray = h.MouseRay;
        if (!GizmoUtils.IntersectPlane(new Float3(0f, 0f, 1f), new Float3(0f, 0f, z), ray.Origin, ray.Direction, out float t))
        {
            hit = Float3.Zero;
            return false;
        }
        hit = ray.Origin + ray.Direction * t;
        return true;
    }

    // ---- handle layout -------------------------------------------------------------------

    private static float ZAngle(Transform t)
    {
        Quaternion q = t.Rotation;
        return MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));
    }

    /// <summary>World rotation, in radians, of whatever the joint's axis and limits are measured against: the connected body, or the world.</summary>
    private static float ReferenceAngle(Joint2D joint)
    {
        Rigidbody2D? other = joint.ConnectedBody;
        return other.IsValid() ? ZAngle(other.Transform) : 0f;
    }

    private static Float3 Dir(float radians) => new(MathF.Cos(radians), MathF.Sin(radians), 0f);

    private static float WrapDegrees(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        else if (degrees <= -180f) degrees += 360f;
        return degrees;
    }

    private static Float3 AnchorWorld(Rigidbody2D body, Joint2D joint)
        => body.Transform.TransformPoint(new Float3(joint.Anchor.X, joint.Anchor.Y, 0f));

    /// <summary>Where the connected anchor is, as far as the editor can tell without a running joint.</summary>
    private static Float3 ConnectedAnchorWorld(Joint2D joint, Float3 anchorWorld)
    {
        // Until the joint has measured it, an automatic connected anchor is this anchor.
        if (joint.AutoConfigureConnectedAnchor && !joint.IsCreated) return anchorWorld;

        Float2 c = joint.ConnectedAnchor;
        Rigidbody2D? other = joint.ConnectedBody;
        if (other is not null)
            return other.IsValid() ? other.Transform.TransformPoint(new Float3(c.X, c.Y, 0f)) : anchorWorld;
        return new Float3(c.X, c.Y, anchorWorld.Z);
    }

    private void Add(Joint2D joint, int jointIndex, Kind kind, Float3 position)
    {
        _handles.Add(new HandleInfo
        {
            Joint = joint,
            Kind = kind,
            Id = ControlId("handle", jointIndex * 8 + (int)kind),
            Position = position,
        });
    }

    private void CollectHandles(GameObject go)
    {
        _handles.Clear();

        int jointIndex = 0;
        foreach (Joint2D joint in go.GetComponents<Joint2D>())
        {
            int index = jointIndex++;
            if (!joint.IsValid()) continue;
            Rigidbody2D? body = joint.AttachedRigidbody;
            if (!body.IsValid()) continue;

            Float3 anchorW = AnchorWorld(body, joint);
            Float3 connectedW = ConnectedAnchorWorld(joint, anchorW);
            float reference = ReferenceAngle(joint);

            Add(joint, index, Kind.Anchor, anchorW);
            if (!joint.AutoConfigureConnectedAnchor) Add(joint, index, Kind.ConnectedAnchor, connectedW);

            switch (joint)
            {
                case RevoluteJoint2D r when r.EnableLimit:
                    Add(joint, index, Kind.LowerLimit, connectedW + Dir(reference + r.LowerAngle * Maths.Deg2Rad) * LimitRadius);
                    Add(joint, index, Kind.UpperLimit, connectedW + Dir(reference + r.UpperAngle * Maths.Deg2Rad) * LimitRadius);
                    break;

                case PrismaticJoint2D p:
                    CollectSlide(joint, index, connectedW, reference + p.Angle * Maths.Deg2Rad, p.EnableLimit, p.LowerTranslation, p.UpperTranslation);
                    break;

                case WheelJoint2D w:
                    CollectSlide(joint, index, connectedW, reference + w.Angle * Maths.Deg2Rad, w.EnableLimit, w.LowerTranslation, w.UpperTranslation);
                    break;

                case DistanceJoint2D d:
                {
                    // The joint acts along the line from the connected anchor to this one; with the anchors together it has no
                    // direction yet, so the handles fan out along +X until it does.
                    Float3 along = anchorW - connectedW;
                    float length = MathF.Sqrt(along.X * along.X + along.Y * along.Y);
                    Float3 dir = length > 1e-4f ? new Float3(along.X / length, along.Y / length, 0f) : new Float3(1f, 0f, 0f);
                    if (!d.AutoConfigureDistance) Add(joint, index, Kind.RestDistance, connectedW + dir * d.Distance);
                    if (d.EnableLimit)
                    {
                        Add(joint, index, Kind.LowerLimit, connectedW + dir * d.MinDistance);
                        Add(joint, index, Kind.UpperLimit, connectedW + dir * d.MaxDistance);
                    }
                    break;
                }
            }
        }
    }

    private void CollectSlide(Joint2D joint, int index, Float3 originW, float axisRadians, bool limited, float lower, float upper)
    {
        Float3 dir = Dir(axisRadians);
        Add(joint, index, Kind.Axis, originW + dir * AxisLength);
        if (!limited) return;
        Add(joint, index, Kind.LowerLimit, originW + dir * lower);
        Add(joint, index, Kind.UpperLimit, originW + dir * upper);
    }

    // ---- applying a drag -----------------------------------------------------------------

    private static float Along(Float3 point, Float3 origin, Float3 dir)
        => (point.X - origin.X) * dir.X + (point.Y - origin.Y) * dir.Y;

    private static float AngleAround(Float3 point, Float3 origin, float referenceRadians)
        => WrapDegrees(MathF.Atan2(point.Y - origin.Y, point.X - origin.X) * Maths.Rad2Deg - referenceRadians * Maths.Rad2Deg);

    /// <summary>Turns a dragged world position into the joint's own value, through its public properties.</summary>
    private static void Apply(Joint2D joint, Kind kind, Float3 world)
    {
        Rigidbody2D? body = joint.AttachedRigidbody;
        if (!body.IsValid()) return;

        Float3 anchorW = AnchorWorld(body, joint);
        Float3 connectedW = ConnectedAnchorWorld(joint, anchorW);
        float reference = ReferenceAngle(joint);

        switch (kind)
        {
            case Kind.Anchor:
            {
                Float3 local = body.Transform.InverseTransformPoint(world);
                joint.Anchor = new Float2(local.X, local.Y);
                break;
            }

            case Kind.ConnectedAnchor:
            {
                Rigidbody2D? other = joint.ConnectedBody;
                if (other.IsValid())
                {
                    Float3 local = other.Transform.InverseTransformPoint(world);
                    joint.ConnectedAnchor = new Float2(local.X, local.Y);
                }
                else
                {
                    joint.ConnectedAnchor = new Float2(world.X, world.Y);
                }
                break;
            }

            case Kind.Axis:
            {
                float degrees = AngleAround(world, connectedW, reference);
                if (joint is PrismaticJoint2D p) p.Angle = degrees;
                else if (joint is WheelJoint2D w) w.Angle = degrees;
                break;
            }

            case Kind.LowerLimit:
            case Kind.UpperLimit:
                ApplyLimit(joint, kind == Kind.LowerLimit, world, anchorW, connectedW, reference);
                break;

            case Kind.RestDistance:
                if (joint is DistanceJoint2D d)
                {
                    Float3 along = anchorW - connectedW;
                    float length = MathF.Sqrt(along.X * along.X + along.Y * along.Y);
                    Float3 dir = length > 1e-4f ? new Float3(along.X / length, along.Y / length, 0f) : new Float3(1f, 0f, 0f);
                    d.Distance = MathF.Max(Along(world, connectedW, dir), MinDistance);
                }
                break;
        }
    }

    private static void ApplyLimit(Joint2D joint, bool lower, Float3 world, Float3 anchorW, Float3 connectedW, float reference)
    {
        switch (joint)
        {
            case RevoluteJoint2D r:
            {
                float degrees = AngleAround(world, connectedW, reference);
                // A limit cannot cross the other one; the component would push it along, which makes a drag feel like it is fighting you.
                if (lower) r.LowerAngle = MathF.Min(degrees, r.UpperAngle);
                else r.UpperAngle = MathF.Max(degrees, r.LowerAngle);
                break;
            }

            case PrismaticJoint2D p:
            {
                Float3 dir = Dir(reference + p.Angle * Maths.Deg2Rad);
                float t = Along(world, connectedW, dir);
                if (lower) p.LowerTranslation = MathF.Min(t, p.UpperTranslation);
                else p.UpperTranslation = MathF.Max(t, p.LowerTranslation);
                break;
            }

            case WheelJoint2D w:
            {
                Float3 dir = Dir(reference + w.Angle * Maths.Deg2Rad);
                float t = Along(world, connectedW, dir);
                if (lower) w.LowerTranslation = MathF.Min(t, w.UpperTranslation);
                else w.UpperTranslation = MathF.Max(t, w.LowerTranslation);
                break;
            }

            case DistanceJoint2D d:
            {
                Float3 along = anchorW - connectedW;
                float length = MathF.Sqrt(along.X * along.X + along.Y * along.Y);
                Float3 dir = length > 1e-4f ? new Float3(along.X / length, along.Y / length, 0f) : new Float3(1f, 0f, 0f);
                float t = MathF.Max(Along(world, connectedW, dir), 0f);
                if (lower) d.MinDistance = MathF.Min(t, d.MaxDistance);
                else d.MaxDistance = MathF.Max(t, d.MinDistance);
                break;
            }
        }
    }

    // ---- drawing -------------------------------------------------------------------------

    private static Color32 ColorOf(Kind kind) => kind switch
    {
        Kind.Anchor => AnchorColor,
        Kind.ConnectedAnchor => ConnectedColor,
        Kind.Axis => AxisColor,
        Kind.RestDistance => RestColor,
        _ => LimitColor,
    };

    private static HandleCap CapOf(Kind kind) => kind switch
    {
        Kind.Anchor => HandleCap.Circle,
        Kind.ConnectedAnchor => HandleCap.Square,
        Kind.RestDistance => HandleCap.Dot,
        _ => HandleCap.Diamond,
    };

    private static Color32 Brighten(Color32 c)
        => new((byte)Math.Min(255, c.R + 70), (byte)Math.Min(255, c.G + 70), (byte)Math.Min(255, c.B + 70), c.A);

    private void DrawHandles(HandleContext h, DragState drag)
    {
        foreach (HandleInfo info in _handles)
        {
            Color32 color = ColorOf(info.Kind);
            bool active = h.IsActive(info.Id);

            // Thin connectors back to where each axis or limit is measured from, so a lone diamond reads as part of its joint.
            if (info.Kind == Kind.Axis || (info.Kind is Kind.LowerLimit or Kind.UpperLimit && info.Joint is RevoluteJoint2D))
            {
                Rigidbody2D? body = info.Joint.AttachedRigidbody;
                if (body.IsValid())
                {
                    Float3 anchorW = AnchorWorld(body, info.Joint);
                    DrawLine(ConnectedAnchorWorld(info.Joint, anchorW), info.Position, color);
                }
            }

            DrawHandleDot(info.Position, active ? Brighten(color) : color, active ? 11f : 8f, CapOf(info.Kind));
        }

        // Read the value out next to the handle being dragged, in the units the inspector uses.
        if (drag.Control.IsValid && drag.Joint != null && drag.Joint.IsValid())
            DrawWorldLabel(drag.LastPosition, ValueLabel(drag.Joint, drag.Kind), ColorOf(drag.Kind));
    }

    private static string ValueLabel(Joint2D joint, Kind kind)
    {
        switch (kind)
        {
            case Kind.Anchor: return $"({joint.Anchor.X:0.##}, {joint.Anchor.Y:0.##})";
            case Kind.ConnectedAnchor: return $"({joint.ConnectedAnchor.X:0.##}, {joint.ConnectedAnchor.Y:0.##})";
            case Kind.Axis:
                return joint is PrismaticJoint2D p ? $"{p.Angle:0.#}\u00B0" : joint is WheelJoint2D w ? $"{w.Angle:0.#}\u00B0" : "";
            case Kind.RestDistance:
                return joint is DistanceJoint2D rest ? $"{rest.Distance:0.###}" : "";
            case Kind.LowerLimit:
                return joint switch
                {
                    RevoluteJoint2D r => $"{r.LowerAngle:0.#}\u00B0",
                    PrismaticJoint2D p => $"{p.LowerTranslation:0.###}",
                    WheelJoint2D w => $"{w.LowerTranslation:0.###}",
                    DistanceJoint2D d => $"{d.MinDistance:0.###}",
                    _ => "",
                };
            case Kind.UpperLimit:
                return joint switch
                {
                    RevoluteJoint2D r => $"{r.UpperAngle:0.#}\u00B0",
                    PrismaticJoint2D p => $"{p.UpperTranslation:0.###}",
                    WheelJoint2D w => $"{w.UpperTranslation:0.###}",
                    DistanceJoint2D d => $"{d.MaxDistance:0.###}",
                    _ => "",
                };
            default: return "";
        }
    }

    // ---- tool strip ----------------------------------------------------------------------

    /// <summary>One toggle that shows or hides the handles, lit while they are shown.</summary>
    public override void OnToolStripGUI(SceneToolContext ctx, Paper paper, string id)
    {
        var font = EditorTheme.DefaultFont!;

        var button = paper.Box($"{id}_handles")
            .Width(24).Height(24).Rounded(EditorTheme.Roundness);
        button = _showHandles ? button.BackgroundColor(EditorTheme.Ink200) : button.BackgroundColor(SColor.Transparent);
        button.Hovered.BackgroundColor(EditorTheme.Ink200).End()
            .Text(EditorIcons.Link, font).TextColor(EditorTheme.Ink500)
            .FontSize(11f).Alignment(TextAlignment.MiddleCenter)
            .OnClick(0, (_, _) => _showHandles = !_showHandles);
    }
}
