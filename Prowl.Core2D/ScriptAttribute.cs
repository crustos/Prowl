// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Core2D;

/// <summary>
/// Marks a class as a script: game code that the scene calls. A script is an arena class (<c>[MaxInstances(N)]</c>) with a
/// <c>public Component Self;</c> field and any of the callbacks Start, Update, FixedUpdate, LateUpdate, OnEnable, OnDisable, OnCollisionBegin2D(Collision2D),
/// OnCollisionEnd2D(Collision2D), OnTriggerEnter2D(Collider2D), OnTriggerStay2D(Collider2D), OnTriggerExit2D(Collider2D), and optionally
/// <c>public void Reset()</c>, which puts a recycled instance back to its starting state.
/// <para/>
/// <c>tools/ccsharp/gen_scripts.py</c> reads the marked classes and writes <c>Scripts.g.cs</c>: a pool and a factory (<c>Scripts.AddName(node)</c>) per
/// script, and <c>Scripts.Invoke(scene)</c>, which a game's loop calls for each call the scene produces. Which callbacks a script has is read from its
/// source at that point, where the .NET engine reads it by reflection when the component is first used.
/// <para/>
/// Under .NET this attribute does nothing: it is only read by the generator.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class ScriptAttribute : Attribute
{
    /// <summary>Execution order: scripts with a lower value run first, ties in the order they were enabled.</summary>
    public int Order { get; set; }
}
