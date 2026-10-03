// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Linq;

using Prowl.Editor.Core;
using Prowl.Editor.GUI.Panels;
using Prowl.Editor.GUI.SceneView;
using Prowl.Editor.Theming;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Editor.GUI;

/// <summary>
/// <i>GameObject &gt; 2D Physics &gt; Joints</i>. Every entry does the obvious thing for what is selected:
/// <list type="bullet">
/// <item>A GameObject with a <see cref="Rigidbody2D"/> is the active selection and another one is selected as well: the joint goes on the
/// active one and connects the other (select the wheel, then the chassis, then choose Wheel).</item>
/// <item>Only one body: the joint goes on it and anchors it to the world, at the body's origin.</item>
/// <item>No body: a new box with a rigidbody and the joint is created, so the menu is a way to try a joint out from an empty scene.</item>
/// </list>
/// With more than two bodies selected the active one and the next selected one are used. The joint starts relaxed, so nothing
/// moves when it is added; drag its handles in the scene view (see <see cref="SceneView.Editors.JointSceneEditor"/>) to place it.
/// </summary>
internal static class Physics2DJointCreators
{
    private const string Root = "GameObject/2D Physics/Joints/";

    // Follows the 2D physics block (50-55) in DefaultGameObjectCreators.
    [MenuItem(Root + "Hinge (Revolute)", priority: 60, Icon = EditorIcons.ArrowsSpin, Separator = true)]
    static void CreateHinge() => Create<RevoluteJoint2D>("Hinge Joint");

    [MenuItem(Root + "Slider (Prismatic)", priority: 61, Icon = EditorIcons.ArrowsLeftRight)]
    static void CreateSlider() => Create<PrismaticJoint2D>("Slider Joint");

    [MenuItem(Root + "Distance (Rod, Spring, Rope)", priority: 62, Icon = EditorIcons.Ruler)]
    static void CreateDistance() => Create<DistanceJoint2D>("Distance Joint");

    [MenuItem(Root + "Wheel", priority: 63, Icon = EditorIcons.Dharmachakra)]
    static void CreateWheel() => Create<WheelJoint2D>("Wheel Joint");

    [MenuItem(Root + "Weld", priority: 64, Icon = EditorIcons.Lock)]
    static void CreateWeld() => Create<WeldJoint2D>("Weld Joint");

    [MenuItem(Root + "Motor", priority: 65, Icon = EditorIcons.Gears)]
    static void CreateMotor() => Create<MotorJoint2D>("Motor Joint");

    [MenuItem(Root + "No Collision (Filter)", priority: 66, Icon = EditorIcons.Ban)]
    static void CreateFilter() => Create<FilterJoint2D>("Filter Joint");

    /// <summary>A filter joint says "these two do not collide", which needs two bodies; offer it only when two are selected.</summary>
    [MenuItem(Root + "No Collision (Filter)", isValidate: true)]
    static bool ValidateFilter()
    {
        Rigidbody2D? owner = BodyOf(MenuContext.ActiveGameObject);
        return owner != null && OtherSelectedBody(owner) != null;
    }

    // ---- shared --------------------------------------------------------------------------

    private static Rigidbody2D? BodyOf(GameObject? go) => go.IsValid() ? go.GetComponent<Rigidbody2D>() : null;

    /// <summary>The first selected rigidbody that is not <paramref name="owner"/>.</summary>
    private static Rigidbody2D? OtherSelectedBody(Rigidbody2D owner)
    {
        foreach (GameObject go in Selection.GetSelected<GameObject>())
        {
            Rigidbody2D? rb = BodyOf(go);
            if (rb != null && !ReferenceEquals(rb, owner)) return rb;
        }
        return null;
    }

    private static void Create<T>(string newObjectName) where T : Joint2D, new()
    {
        Rigidbody2D? owner = BodyOf(MenuContext.ActiveGameObject);
        if (owner == null)
        {
            CreateOnNewBody<T>(newObjectName);
            return;
        }
        AddToBody<T>(owner, OtherSelectedBody(owner));
    }

    /// <summary>
    /// Gives a joint its starting configuration. Shared by the menu and by redo, so replaying an undone add produces
    /// exactly what the first one did.
    /// </summary>
    private static void Setup(Joint2D joint, Rigidbody2D owner, Rigidbody2D? other)
    {
        joint.ConnectedBody = other;

        // A distance joint measures its rest length between its two anchors, so with both at the body's origin it would hold a
        // length of nothing and pin the body to its anchor. Run the rod from the body's origin to the other body's origin, or
        // to a point above it when it hangs from the world.
        if (joint is DistanceJoint2D)
        {
            joint.AutoConfigureConnectedAnchor = false;
            if (other != null)
            {
                joint.ConnectedAnchor = Float2.Zero;
            }
            else
            {
                Float3 p = owner.Transform.Position;
                joint.ConnectedAnchor = new Float2(p.X, p.Y + 2f);
            }
        }
    }

    /// <summary>Adds the joint to a body that already exists, as one undo step.</summary>
    private static void AddToBody<T>(Rigidbody2D owner, Rigidbody2D? other) where T : Joint2D, new()
    {
        GameObject go = owner.GameObject;
        T joint = go.AddComponent<T>();
        Setup(joint, owner, other);

        // Undo is recorded by hand rather than through the Add Component helper, which serializes the component the moment it is
        // added: the connection made just above would be lost on redo. Redo here replays the same setup instead.
        Guid goId = go.Identifier;
        Guid componentId = joint.Identifier;
        Guid? otherId = other?.GameObject.Identifier;
        Undo.RegisterAction("Add Joint",
            undo: () => Undo.FindGO(goId)?.RemoveComponent(componentId),
            redo: () =>
            {
                GameObject? g = Undo.FindGO(goId);
                Rigidbody2D? body = BodyOf(g);
                if (g == null || body == null) return;
                T again = g.AddComponent<T>();
                again.Identifier = componentId;
                Rigidbody2D? connected = otherId.HasValue ? BodyOf(Undo.FindGO(otherId.Value)) : null;
                Setup(again, body, connected);
            });

        Selection.Select(go);
        EditorSceneManager.MarkDirty();
    }

    /// <summary>Makes a box with a rigidbody and the joint, for trying one out. Creating the object is itself the undo step.</summary>
    private static void CreateOnNewBody<T>(string name) where T : Joint2D, new()
    {
        GameObject go = HierarchyPanel.CreateGameObject(name, MenuContext.ActiveGameObject);
        Rigidbody2D body = go.AddComponent<Rigidbody2D>();
        go.AddComponent<BoxCollider2D>();
        T joint = go.AddComponent<T>();
        Setup(joint, body, null);
    }
}
