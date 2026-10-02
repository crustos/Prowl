// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// Code that touches "the physics world" without caring which one (particle collisions, today) names these aliases, so a
// single #if lives here instead of one in every file. 2D is the default; 3D is opt-in (see ProwlPhysics3D in
// Directory.Build.props) and is not maintained.
#if PROWL_PHYSICS_3D
global using ParticleCollider = Prowl.Runtime.Collider;
global using ParticlePhysicsWorld = Prowl.Runtime.PhysicsWorld;
#else
global using ParticleCollider = Prowl.Runtime.Collider2D;
global using ParticlePhysicsWorld = Prowl.Runtime.PhysicsWorld2D;
#endif
