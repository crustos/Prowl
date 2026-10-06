// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Editor.Core;
using Prowl.Editor.GUI.Panels;
using Prowl.Editor.Theming;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Editor.GUI;

/// <summary>
/// <i>GameObject &gt; 2D Physics &gt; Destruction</i>: ground you can dig (<see cref="PixelTerrain2D"/>) and things that break into
/// fragments (<see cref="Explodable2D"/>). Both are built from <c>Prowl.Runtime/Destruction2D</c>, the code the 2D player shares.
/// Their settings are in the inspector (hover a field for what it does); the Explode, Rebuild and Dig Test Crater buttons work while playing.
/// </summary>
internal static class DestructionCreators
{
    private const string Root = "GameObject/2D Physics/Destruction/";

    // Follows the joints (60-66) in Physics2DJointCreators.
    [MenuItem(Root + "Destructible Terrain", priority: 70, Icon = EditorIcons.Mountain, Separator = true)]
    static void CreateTerrain()
    {
        var go = HierarchyPanel.CreateGameObject("Destructible Terrain", MenuContext.ActiveGameObject);
        var terrain = go.AddComponent<PixelTerrain2D>();
        // The position is the lower-left corner: start it so the terrain is centred on the origin.
        Float2 size = terrain.SizeInUnits;
        go.Transform.Position = new Float3(-size.X * 0.5f, -size.Y * 0.5f, 0f);
    }

    [MenuItem(Root + "Explodable Box", priority: 71, Icon = EditorIcons.Bomb)]
    static void CreateExplodableBox()
    {
        var go = HierarchyPanel.CreateGameObject("Explodable Box", MenuContext.ActiveGameObject);
        go.AddComponent<Rigidbody2D>();
        go.AddComponent<BoxCollider2D>();
        go.AddComponent<Explodable2D>();
    }

    [MenuItem(Root + "Explodable Polygon", priority: 72, Icon = EditorIcons.Bomb)]
    static void CreateExplodablePolygon()
    {
        var go = HierarchyPanel.CreateGameObject("Explodable Polygon", MenuContext.ActiveGameObject);
        go.AddComponent<Rigidbody2D>();
        var poly = go.AddComponent<PolygonCollider2D>();
        var points = new Float2[6];
        for (int i = 0; i < points.Length; i++)
        {
            float a = i * MathF.PI * 2f / points.Length;
            points[i] = new Float2(MathF.Cos(a) * 0.6f, MathF.Sin(a) * 0.6f);
        }
        poly.Points = points;
        go.AddComponent<Explodable2D>();
    }

    /// <summary>Adds an Explodable2D to what is selected, when it has a box or polygon collider to take the outline from.</summary>
    [MenuItem(Root + "Make Selected Explodable", priority: 73, Icon = EditorIcons.Bomb)]
    static void MakeSelectedExplodable()
    {
        GameObject? go = MenuContext.ActiveGameObject;
        if (go.IsValid() && !go.GetComponent<Explodable2D>().IsValid())
            go.AddComponent<Explodable2D>();
    }

    [MenuItem(Root + "Make Selected Explodable", isValidate: true)]
    static bool ValidateMakeSelectedExplodable()
    {
        GameObject? go = MenuContext.ActiveGameObject;
        return go.IsValid()
            && !go.GetComponent<Explodable2D>().IsValid()
            && (go.GetComponent<BoxCollider2D>().IsValid() || go.GetComponent<PolygonCollider2D>().IsValid());
    }
}
