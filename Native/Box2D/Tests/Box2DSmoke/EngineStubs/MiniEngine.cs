// A headless stand-in for the slice of the Prowl engine that the 2D physics components touch.
//
// Prowl.Runtime cannot be built outside a machine with NuGet access (Prowl.Echo, Aperture, Silk.NET ...), so the
// real component files (Rigidbody2D, Collider2D, PhysicsWorld2D ...) are compiled UNMODIFIED against these stubs and
// run against the real native library. This verifies the component logic and every engine API *name and signature*
// those files use, as far as the stubs are faithful. Each stub mirrors the real member it stands for (checked against
// the engine source); where behaviour is simplified it is said here:
//
//   * Transform composes position / Z-rotation / scale through a parent chain in 2D. Real Transform is a full 3D TRS.
//   * Components enable in the order they were added, children after their parent. Real activation is richer.
//   * SceneDispatcher calls every component's handler; the real one filters by which handlers a type overrides.
//
// None of these change what the 2D components do with them.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Vector
{
    public struct Float2
    {
        public float X, Y;
        public Float2(float x, float y) { X = x; Y = y; }
        public static readonly Float2 Zero = new(0, 0);
        public static Float2 operator +(Float2 a, Float2 b) => new(a.X + b.X, a.Y + b.Y);
        public static Float2 operator -(Float2 a, Float2 b) => new(a.X - b.X, a.Y - b.Y);
        public static Float2 operator *(Float2 a, float s) => new(a.X * s, a.Y * s);
        public override readonly string ToString() => $"({X:F3}, {Y:F3})";
    }

    public struct Float3 : IEquatable<Float3>
    {
        public float X, Y, Z;
        public Float3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static readonly Float3 Zero = new(0, 0, 0);
        public static readonly Float3 One = new(1, 1, 1);
        public static readonly Float3 UnitZ = new(0, 0, 1);
        public static Float3 operator *(Float3 a, Float3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        public static Float3 operator *(Float3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Float3 operator +(Float3 a, Float3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Float3 operator -(Float3 a, Float3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public readonly bool Equals(Float3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override readonly bool Equals(object? obj) => obj is Float3 f && Equals(f);
        public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override readonly string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }

    /// <summary>Stand-in for the engine's Color, which lives in the Prowl.Vector package. Only the named members gizmos use.</summary>
    public readonly struct Color : IEquatable<Color>
    {
        public readonly string Name;
        private Color(string name) { Name = name; }
        public static readonly Color Green = new("Green"), Red = new("Red"), Yellow = new("Yellow"), Cyan = new("Cyan"), White = new("White");
        public readonly bool Equals(Color o) => Name == o.Name;
        public override readonly bool Equals(object? o) => o is Color c && Equals(c);
        public override readonly int GetHashCode() => Name?.GetHashCode() ?? 0;
        public static bool operator ==(Color a, Color b) => a.Equals(b);
        public static bool operator !=(Color a, Color b) => !a.Equals(b);
        public override readonly string ToString() => Name ?? "(none)";
    }

    public struct Quaternion
    {
        public float X, Y, Z, W;
        public Quaternion(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Quaternion Identity = new(0, 0, 0, 1);
    }
}

namespace Prowl.Echo
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class SerializeFieldAttribute : Attribute { }
}

namespace Prowl.Runtime
{
    public static class Maths
    {
        public const float Deg2Rad = MathF.PI / 180f;
        public const float Rad2Deg = 180f / MathF.PI;
    }

    public static class Time
    {
        public static float FixedDeltaTime = 1f / 60f;
        public static float FixedAccumulator;
        public static float FixedAlpha => FixedDeltaTime > 0f ? Math.Clamp(FixedAccumulator / FixedDeltaTime, 0f, 1f) : 1f;
    }

    public static class Debug
    {
        public static readonly List<string> Errors = new();
        private static readonly HashSet<string> s_once = new();
        public static void LogError(string message) => Errors.Add(message);
        public static void LogErrorOnce(string key, string message) { if (s_once.Add(key)) Errors.Add(message); }
        public static void ResetForTests() { Errors.Clear(); s_once.Clear(); Lines.Clear(); }

        /// <summary>Every line a gizmo drew since the last reset (dashed lines are recorded as a single line).</summary>
        public static readonly List<(Float3 A, Float3 B, Color Color, bool Dashed)> Lines = new();
        public static void DrawLine(Float3 start, Float3 end, Color color) => Lines.Add((start, end, color, false));
        public static void DrawDashedLine(Float3 from, Float3 to, Color color, int dashes = 8) => Lines.Add((from, to, color, true));
    }

    /// <summary>Same representation as the real one: it stores the EXCLUDED layers so that default(LayerMask) is everything.</summary>
    public struct LayerMask
    {
        private uint excluded;
        public static readonly LayerMask Everything = default;
        public static readonly LayerMask Nothing = FromMask(0);
        public readonly uint Mask => ~excluded;
        public static LayerMask FromMask(uint mask) => new() { excluded = ~mask };
        public readonly bool HasLayer(int index) => (excluded & (1u << index)) == 0;
        public void SetLayer(int index) => excluded &= ~(1u << index);
        public void RemoveLayer(int index) => excluded |= 1u << index;
    }

    public class EngineObject
    {
        public bool IsDisposed { get; protected set; }
        public string Name { get; set; } = "";
        internal void MarkDisposed() => IsDisposed = true;
    }

    public static class EngineObjectExtensions
    {
        public static bool IsNotValid([NotNullWhen(false)] this EngineObject? obj) => obj is null || obj.IsDisposed;
        public static bool IsValid([NotNullWhen(true)] this EngineObject? obj) => obj is not null && !obj.IsDisposed;
    }

    public class MonoBehaviour : EngineObject
    {
        internal GameObject _go = null!;
        public GameObject GameObject => _go;
        public Transform Transform => _go.Transform;
        private bool _enabled = true;

        /// <summary>Toggling fires OnEnable / OnDisable if that changes whether the component is live, as the real one does.</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                bool was = EnabledInHierarchy;
                _enabled = value;
                bool now = EnabledInHierarchy;
                if (was == now) return;
                if (now) OnEnable(); else OnDisable();
            }
        }

        public bool EnabledInHierarchy => !IsDisposed && _enabled && _go.ActiveInHierarchy;
        internal bool ShouldExecuteGameplay => true;
        private protected void AssertOwner() { }

        public virtual void OnEnable() { }
        public virtual void OnDisable() { }
        public virtual void OnValidate() { }
        public virtual void Update() { }
        public virtual void FixedUpdate() { }
        public virtual void DrawGizmos() { }
        public virtual void DrawGizmosSelected() { }

        public virtual void OnCollisionBegin2D(Collision2D collision) { }
        public virtual void OnCollisionEnd2D(Collision2D collision) { }
        public virtual void OnTriggerEnter2D(Collider2D other) { }
        public virtual void OnTriggerStay2D(Collider2D other) { }
        public virtual void OnTriggerExit2D(Collider2D other) { }

        internal void InternalOnCollisionBegin2D(Collision2D c) => OnCollisionBegin2D(c);
        internal void InternalOnCollisionEnd2D(Collision2D c) => OnCollisionEnd2D(c);
        internal void InternalOnTriggerEnter2D(Collider2D o) => OnTriggerEnter2D(o);
        internal void InternalOnTriggerStay2D(Collider2D o) => OnTriggerStay2D(o);
        internal void InternalOnTriggerExit2D(Collider2D o) => OnTriggerExit2D(o);

        public T? GetComponent<T>() where T : MonoBehaviour => GameObject.GetComponent<T>();
        public T? GetComponentInParent<T>(bool includeSelf = true) where T : MonoBehaviour => GameObject.GetComponentInParent<T>(includeSelf);
        public IEnumerable<T> GetComponentsInParent<T>(bool includeSelf = true) where T : MonoBehaviour => GameObject.GetComponentsInParent<T>(includeSelf);
        public IEnumerable<T> GetComponentsInChildren<T>(bool includeSelf = true, bool includeInactive = false) where T : MonoBehaviour
            => GameObject.GetComponentsInChildren<T>(includeSelf, includeInactive);
    }

    public sealed class GameObject : EngineObject
    {
        internal readonly List<MonoBehaviour> _components = new();
        private readonly List<GameObject> _children = new();
        private GameObject? _parent;
        private bool _active;

        internal GameObject(Scene scene, string name)
        {
            Scene = scene;
            Name = name;
            Transform = new Transform(this);
        }

        public Scene Scene { get; }
        public Transform Transform { get; }
        public int LayerIndex;
        public bool ActiveInHierarchy => !IsDisposed && _active && (_parent == null || _parent.ActiveInHierarchy);

        public void SetParent(GameObject? parent)
        {
            _parent?._children.Remove(this);
            _parent = parent;
            parent?._children.Add(this);
            Transform.SetParent(parent?.Transform);
        }

        public T AddComponent<T>() where T : MonoBehaviour, new()
        {
            var c = new T { _go = this };
            _components.Add(c);
            if (ActiveInHierarchy) c.OnEnable();
            return c;
        }

        public T? GetComponent<T>() where T : MonoBehaviour
        {
            foreach (MonoBehaviour c in _components) if (c is T t) return t;
            return null;
        }

        public T? GetComponentInParent<T>(bool includeSelf = true) where T : MonoBehaviour
        {
            foreach (T t in GetComponentsInParent<T>(includeSelf)) return t;
            return null;
        }

        public IEnumerable<T> GetComponentsInParent<T>(bool includeSelf = true) where T : MonoBehaviour
        {
            for (GameObject? go = includeSelf ? this : _parent; go != null; go = go._parent)
                foreach (MonoBehaviour c in go._components.ToArray())
                    if (c is T t) yield return t;
        }

        public IEnumerable<T> GetComponentsInChildren<T>(bool includeSelf = true, bool includeInactive = false) where T : MonoBehaviour
        {
            var result = new List<T>();
            Collect(this, includeSelf, includeInactive, result);
            return result;
        }

        private static void Collect<T>(GameObject go, bool self, bool inactive, List<T> into) where T : MonoBehaviour
        {
            if (!inactive && !go.ActiveInHierarchy) return;
            if (self) foreach (MonoBehaviour c in go._components) if (c is T t) into.Add(t);
            foreach (GameObject child in go._children) Collect(child, true, inactive, into);
        }

        /// <summary>Activating enables components in add order, parent before children.</summary>
        public void SetActive(bool active)
        {
            if (_active == active) return;
            bool wasLive = ActiveInHierarchy;
            _active = active;
            bool isLive = ActiveInHierarchy;
            if (wasLive != isLive) Propagate(isLive);
        }

        private void Propagate(bool live)
        {
            if (live) foreach (MonoBehaviour c in _components.ToArray()) { if (c.Enabled && !c.IsDisposed) c.OnEnable(); }
            else foreach (MonoBehaviour c in _components.ToArray()) { if (c.Enabled && !c.IsDisposed) c.OnDisable(); }
            foreach (GameObject child in _children.ToArray())
                if (child._active) child.Propagate(live);
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            foreach (GameObject child in _children.ToArray()) child.Dispose();
            if (ActiveInHierarchy)
                foreach (MonoBehaviour c in _components.ToArray()) if (c.Enabled) c.OnDisable();
            IsDisposed = true;
            foreach (MonoBehaviour c in _components) c.MarkDisposed();
            _parent?._children.Remove(this);
            Scene.Remove(this);
        }
    }

    internal static class SceneDispatcher
    {
        private static void Each(GameObject go, Action<MonoBehaviour> call)
        {
            foreach (MonoBehaviour c in go._components.ToArray())
                if (c.EnabledInHierarchy) call(c);
        }

        public static void CollisionBegin2D(GameObject go, Collision2D collision) { var c = collision; Each(go, m => m.InternalOnCollisionBegin2D(c)); }
        public static void CollisionEnd2D(GameObject go, Collision2D collision) { var c = collision; Each(go, m => m.InternalOnCollisionEnd2D(c)); }
        public static void TriggerEnter2D(GameObject go, Collider2D other) => Each(go, m => m.InternalOnTriggerEnter2D(other));
        public static void TriggerStay2D(GameObject go, Collider2D other) => Each(go, m => m.InternalOnTriggerStay2D(other));
        public static void TriggerExit2D(GameObject go, Collider2D other) => Each(go, m => m.InternalOnTriggerExit2D(other));
    }
}

// Transform lives in Prowl.Vector in the real engine (Prowl.Runtime/Math/Transform.cs). Mirrored so a missing `using`
// in the real files is a compile error here, not a surprise on the first real build.
namespace Prowl.Vector
{
    using Prowl.Runtime;

    public sealed class Transform
    {
        private Float3 _pos, _scale = Float3.One;
        private float _angle;
        private uint _version;

        public Transform(GameObject go) { GameObject = go; }
        public GameObject GameObject { get; }
        public uint Version => _version;
        public Transform? Parent { get; private set; }

        internal void SetParent(Transform? parent) { Parent = parent; _version++; }

        public Float3 LocalPosition { get => _pos; set { _pos = value; _version++; } }
        public Float3 LocalScale { get => _scale; set { _scale = value; _version++; } }
        public float LocalAngle { get => _angle; set { _angle = value; _version++; } }

        public Float3 LossyScale => Parent == null ? _scale : Parent.LossyScale * _scale;
        private float WorldAngle => Parent == null ? _angle : Parent.WorldAngle + _angle;

        public Float3 Position
        {
            get => Parent == null ? _pos : Parent.TransformPoint(_pos);
            set => LocalPosition = Parent == null ? value : Parent.InverseTransformPoint(value);
        }

        public Quaternion Rotation
        {
            get { float h = WorldAngle * 0.5f; return new Quaternion(0, 0, MathF.Sin(h), MathF.Cos(h)); }
            set
            {
                float a = MathF.Atan2(2f * (value.W * value.Z + value.X * value.Y), 1f - 2f * (value.Y * value.Y + value.Z * value.Z));
                LocalAngle = Parent == null ? a : a - Parent.WorldAngle;
            }
        }

        public Float3 TransformPoint(Float3 p)
        {
            Float3 s = p * LossyScale;
            float c = MathF.Cos(WorldAngle), sn = MathF.Sin(WorldAngle);
            return Position + new Float3(c * s.X - sn * s.Y, sn * s.X + c * s.Y, s.Z);
        }

        public Float3 InverseTransformPoint(Float3 w)
        {
            Float3 d = w - Position;
            float c = MathF.Cos(-WorldAngle), sn = MathF.Sin(-WorldAngle);
            Float3 r = new(c * d.X - sn * d.Y, sn * d.X + c * d.Y, d.Z);
            Float3 ls = LossyScale;
            return new Float3(r.X / ls.X, r.Y / ls.Y, r.Z / ls.Z);
        }
    }
}

// Scene lives in Prowl.Runtime.Resources (Prowl.Runtime/Resources/Scene.cs).
namespace Prowl.Runtime.Resources
{
    using System.Collections.Generic;
    using Prowl.Vector;

    public sealed class Scene : EngineObject
    {
        private readonly List<GameObject> _objects = new();

        public Scene(string name) { Name = name; }

        public PhysicsWorld2D Physics2D { get; } = new();

        public GameObject Create(string name, GameObject? parent = null, bool active = true)
        {
            var go = new GameObject(this, name);
            _objects.Add(go);
            if (parent != null) go.SetParent(parent);
            if (active) go.SetActive(true);
            return go;
        }

        internal void Remove(GameObject go) => _objects.Remove(go);

        /// <summary>Snapshot of every GameObject, for tests that activate a whole setup at once.</summary>
        internal IEnumerable<GameObject> AllForTests() => _objects.ToArray();

        /// <summary>Per-frame update of every live component, in creation order.</summary>
        public void Update()
        {
            foreach (GameObject go in _objects.ToArray())
                foreach (MonoBehaviour c in go._components.ToArray())
                    if (c.EnabledInHierarchy) c.Update();
        }

        public void FixedUpdate()
        {
            foreach (GameObject go in _objects.ToArray())
                foreach (MonoBehaviour c in go._components.ToArray())
                    if (c.EnabledInHierarchy) c.FixedUpdate();
            Physics2D.Update();
        }

        public void Dispose()
        {
            foreach (GameObject go in _objects.ToArray()) go.Dispose();
            Physics2D.ReleaseIfIdle();
            IsDisposed = true;
        }
    }
}
