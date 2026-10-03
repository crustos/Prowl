// Prowl <-> Box2D-Packed shim.
//
// Why this exists instead of binding box2d.h directly:
//   * The fork's *Def structs use `bool x : 1` bitfields. Bitfield layout is implementation-defined
//     (GCC and MSVC disagree), so they cannot be mirrored safely from C#.
//   * Prowl has 32 layers and a 32x32 collision matrix; Box2D filters are 16-bit. The matrix is
//     evaluated natively here (custom filter), so there is no managed callback per broad-phase pair.
//   * One managed->native crossing per frame: pb2_step() steps the world and returns every event
//     (body moves, contacts with manifold data resolved, sensor overlaps) in flat, fixed-layout arrays.
//
// ABI rules: no bitfields, no bool, no pointers except where a field is explicitly intptr_t, every
// struct is made of 4-byte fields so its layout is identical on every compiler and on 32/64-bit
// (except the pointer fields of PB2StepInfo, which C# mirrors with nint). pb2_abi() lets the managed
// side assert all of this at startup.
//
// Handles are the fork's 4-byte ids passed as uint32_t. 0 is null.
// userData packing (per shape):  (layer << 24) | (colliderIndex + 1)     [colliderIndex < 2^24 - 1]
// userData (per body):           bodyIndex + 1
// userData (per joint):          jointIndex + 1

#ifndef PROWL_BOX2D_H
#define PROWL_BOX2D_H

#include <stdint.h>

#if defined( _WIN32 )
#define PB2_API __declspec( dllexport )
#else
#define PB2_API __attribute__( ( visibility( "default" ) ) )
#endif

#ifdef __cplusplus
extern "C" {
#endif

// ---- binding markers -------------------------------------------------------------------------
//
// A bare `T*` does not say whether the callee reads it, writes it, or whether it is one record or an array, and bindings for languages
// without pointers need to know. These markers say, in front of the parameter. They expand to NOTHING: the C ABI is exactly what it was.
// tools/ccsharp/gen_pb2.py reads this header and generates the managed bindings (for .NET and for the C# -> C build) from it, and refuses
// a pointer parameter that is not marked, so a new function cannot be added without saying what its pointers mean.
//
//   PB2_IN       a record the callee reads, passed by pointer         (`const PB2JointDef*`)
//   PB2_OUT      a record the callee fills in                          (`PB2StepInfo*`)
//   PB2_IN_ARR   an array the callee reads; its length is another parameter
//   PB2_OUT_ARR  an array the callee writes; its length is another parameter, or fixed and documented at the declaration
//   PB2_VIEW(T, countField)   in front of an `intptr_t` field of a record: the field points at an array of `T`, `countField` long
#define PB2_IN
#define PB2_OUT
#define PB2_IN_ARR
#define PB2_OUT_ARR
#define PB2_VIEW( type, countField )

#define PB2_ABI_VERSION 2
#define PB2_LAYER_COUNT 32

// ---- fixed-layout records ------------------------------------------------------------------

typedef struct PB2BodyMove // 24 bytes
{
	int32_t bodyIndex; // managed index given to pb2_body_create
	float x, y;
	float c, s; // rotation: cos, sin
	int32_t fellAsleep;
} PB2BodyMove;

#define PB2_EVENT_BEGIN 1
#define PB2_EVENT_END 2

typedef struct PB2ContactEvent // 36 bytes
{
	int32_t colliderA, colliderB; // managed collider indices
	float px, py; // world contact point (begin only)
	float nx, ny; // normal pointing A -> B (begin only)
	float impulse; // total normal impulse after the solve (begin only)
	int32_t flags; // PB2_EVENT_BEGIN / PB2_EVENT_END
	int32_t bodyA, bodyB; // managed body indices (-1 if the shape's body is unmanaged)
} PB2ContactEvent;

typedef struct PB2SensorEvent // 12 bytes
{
	int32_t sensorCollider, visitorCollider;
	int32_t flags; // PB2_EVENT_BEGIN / PB2_EVENT_END
} PB2SensorEvent;

typedef struct PB2StepInfo
{
	int32_t moveCount;
	int32_t contactCount; // begin events first, then end events
	int32_t contactBeginCount;
	int32_t sensorCount;
	int32_t awakeBodyCount;
	int32_t jointEventCount; // joints whose force or torque exceeded their threshold this step
	PB2_VIEW( PB2BodyMove, moveCount ) intptr_t moves; // PB2BodyMove*      valid until the next pb2_step
	PB2_VIEW( PB2ContactEvent, contactCount ) intptr_t contacts; // PB2ContactEvent*
	PB2_VIEW( PB2SensorEvent, sensorCount ) intptr_t sensors; // PB2SensorEvent*
	PB2_VIEW( int32_t, jointEventCount ) intptr_t joints; // int32_t*          managed joint indices (the jointIndex given at creation)
} PB2StepInfo;

typedef struct PB2TransformSet // 20 bytes. Batched teleport / kinematic move.
{
	uint32_t body;
	float x, y, angle; // angle in radians
	int32_t mode; // 0 = teleport, 1 = kinematic target (reaches pose over this step's dt)
} PB2TransformSet;

typedef struct PB2RayHit // 28 bytes
{
	int32_t collider;
	int32_t body;
	float px, py;
	float nx, ny;
	float fraction;
} PB2RayHit;


// ---- joints ---------------------------------------------------------------------------------
//
// One record describes every joint type. The 4-byte-field rule applies (no bitfields, no bool), which is the reason this
// is a shim record and not b2*JointDef. Which p[] slot means what depends on `type`; the table below is the contract and
// the managed side (PB2JointDef + the Joint2D components) follows it exactly. Angles are radians.
//
//   PB2_JOINT_DISTANCE   p0 length   p1 minLength  p2 maxLength  p3 hertz  p4 dampingRatio  p5 maxMotorForce  p6 motorSpeed
//   PB2_JOINT_REVOLUTE   p0 targetAngle  p1 hertz  p2 dampingRatio  p3 lowerAngle  p4 upperAngle  p5 maxMotorTorque  p6 motorSpeed
//   PB2_JOINT_PRISMATIC  p0 hertz  p1 dampingRatio  p2 targetTranslation  p3 lowerTranslation  p4 upperTranslation
//                        p5 maxMotorForce  p6 motorSpeed
//   PB2_JOINT_WHEEL      p0 hertz  p1 dampingRatio  p2 lowerTranslation  p3 upperTranslation  p4 maxMotorTorque  p5 motorSpeed
//   PB2_JOINT_WELD       p0 linearHertz  p1 angularHertz  p2 linearDampingRatio  p3 angularDampingRatio   (hertz 0 = rigid)
//   PB2_JOINT_MOTOR      p0 linearVelocityX  p1 linearVelocityY  p2 maxVelocityForce  p3 angularVelocity  p4 maxVelocityTorque
//                        p5 linearHertz  p6 linearDampingRatio  p7 maxSpringForce  p8 angularHertz  p9 angularDampingRatio
//                        p10 maxSpringTorque
//   PB2_JOINT_FILTER     (none; the two bodies simply do not collide)
//
// Flags: PB2_JF_SPRING / PB2_JF_LIMIT / PB2_JF_MOTOR switch the spring, limit and motor of the types that have them.
//
// Frames: a joint is two local frames, one per body, each a position plus an angle in that body's own space (the origin,
// not the centre of mass). The frame's x axis is the slide axis of a prismatic or wheel joint. When bodyA is 0 the joint
// is anchored to the world, and frame A is then in world coordinates.
//
// Which body is which matters, because Box2D measures a joint from A to B: a revolute joint's angle is B's rotation
// relative to A, a prismatic or wheel joint's translation is B's slide along A's x axis. Managed code therefore makes the
// body that owns the joint B, and what it is connected to (another body, or the world) A.

#define PB2_JOINT_DISTANCE 0
#define PB2_JOINT_REVOLUTE 1
#define PB2_JOINT_PRISMATIC 2
#define PB2_JOINT_WHEEL 3
#define PB2_JOINT_WELD 4
#define PB2_JOINT_MOTOR 5
#define PB2_JOINT_FILTER 6

#define PB2_JF_SPRING 1u
#define PB2_JF_LIMIT 2u
#define PB2_JF_MOTOR 4u
#define PB2_JF_COLLIDE_CONNECTED 8u

#define PB2_JOINT_PARAMS 12

typedef struct PB2JointDef // 100 bytes
{
	uint32_t bodyA; // native body id; 0 = anchored to the world
	uint32_t bodyB; // native body id; required
	int32_t jointIndex; // managed index, handed back in PB2StepInfo::joints
	int32_t type; // PB2_JOINT_*
	uint32_t flags; // PB2_JF_*
	float ax, ay, aAngle; // frame on body A (world coordinates when bodyA == 0)
	float bx, by, bAngle; // frame on body B
	float forceThreshold; // a joint event is raised above this force; FLT_MAX or more = never
	float torqueThreshold; // ... and above this torque
	float p[PB2_JOINT_PARAMS];
} PB2JointDef;

// ---- lifecycle / ABI -----------------------------------------------------------------------

// Fills out[0..8] with sizeof() of the records above plus PB2_ABI_VERSION; managed code compares.
// out: [0]=version [1]=BodyMove [2]=ContactEvent [3]=SensorEvent [4]=StepInfo [5]=TransformSet [6]=RayHit [7]=sizeof(void*)
//      [8]=JointDef
PB2_API void pb2_abi( PB2_OUT_ARR int32_t* out16 );

PB2_API void pb2_world_create( float gravityX, float gravityY, int workerCount );
PB2_API void pb2_world_destroy( void );
PB2_API void pb2_world_set_gravity( float x, float y );
PB2_API void pb2_world_set_layer_matrix( PB2_IN_ARR const uint32_t* rows32 ); // rows[a] bit b == layers a,b collide

// Steps the world and gathers all events. One crossing per frame.
PB2_API void pb2_step( float dt, int subSteps, PB2_OUT PB2StepInfo* info );

// Applies many teleports / kinematic moves in one crossing. dt is used by mode 1.
PB2_API void pb2_bodies_set_transforms( PB2_IN_ARR const PB2TransformSet* sets, int count, float dt );

// ---- bodies --------------------------------------------------------------------------------

#define PB2_BODY_STATIC 0
#define PB2_BODY_KINEMATIC 1
#define PB2_BODY_DYNAMIC 2

#define PB2_BF_BULLET 1u
#define PB2_BF_NO_SLEEP 2u
#define PB2_BF_LOCK_X 4u
#define PB2_BF_LOCK_Y 8u
#define PB2_BF_LOCK_ROT 16u
#define PB2_BF_DISABLED 32u

PB2_API uint32_t pb2_body_create( int type, float x, float y, float angle, int32_t bodyIndex, float gravityScale,
								  float linearDamping, float angularDamping, uint32_t flags );
PB2_API void pb2_body_destroy( uint32_t body );
PB2_API void pb2_body_set_transform( uint32_t body, float x, float y, float angle );
PB2_API void pb2_body_set_type( uint32_t body, int type );
PB2_API void pb2_body_set_flags( uint32_t body, uint32_t flags ); // bullet / lock bits / disabled
PB2_API void pb2_body_set_damping( uint32_t body, float linear, float angular );
PB2_API void pb2_body_set_gravity_scale( uint32_t body, float scale );
PB2_API void pb2_body_set_velocity( uint32_t body, float vx, float vy, float angularVelocity );
PB2_API void pb2_body_set_mass( uint32_t body, float mass ); // keeps shape-derived center, scales inertia
PB2_API void pb2_body_set_awake( uint32_t body, int awake );
PB2_API void pb2_body_apply_force( uint32_t body, float fx, float fy, int hasPoint, float px, float py );
PB2_API void pb2_body_apply_impulse( uint32_t body, float ix, float iy, int hasPoint, float px, float py );
PB2_API void pb2_body_apply_torque( uint32_t body, float torque, int asImpulse );
// out[0..9] = x, y, cos, sin, vx, vy, angularVelocity, mass, inertia, awake
PB2_API void pb2_body_get_state( uint32_t body, PB2_OUT_ARR float* out10 );

// ---- shapes (one per collider; chain creates several segments sharing the collider index) --

#define PB2_SF_SENSOR 1u

PB2_API uint32_t pb2_shape_create_circle( uint32_t body, int32_t colliderIndex, int layer, float cx, float cy, float radius,
										  float density, float friction, float restitution, uint32_t flags );
PB2_API uint32_t pb2_shape_create_box( uint32_t body, int32_t colliderIndex, int layer, float halfW, float halfH, float ox,
									   float oy, float angle, float cornerRadius, float density, float friction,
									   float restitution, uint32_t flags );
PB2_API uint32_t pb2_shape_create_capsule( uint32_t body, int32_t colliderIndex, int layer, float x1, float y1, float x2,
										   float y2, float radius, float density, float friction, float restitution,
										   uint32_t flags );
// Convex hull is computed natively; returns 0 if the points do not form a valid hull.
PB2_API uint32_t pb2_shape_create_polygon( uint32_t body, int32_t colliderIndex, int layer, PB2_IN_ARR const float* xy, int pointCount,
										   float radius, float density, float friction, float restitution, uint32_t flags );
// Two-sided edges (what a Unity-style EdgeCollider2D means): one segment shape per edge, each carrying the
// collider index and layer. Writes (n-1) shape ids (n if isLoop) to outShapes and returns that count, 0 on bad
// input. The caller owns the ids and destroys each with pb2_shape_destroy. Segments have no ghost vertices, so a
// character sliding along a flat run of them can catch on the seams; use pb2_chain_create where that matters.
PB2_API int pb2_segments_create( uint32_t body, int32_t colliderIndex, int layer, PB2_IN_ARR const float* xy, int pointCount, int isLoop,
								 float friction, float restitution, PB2_OUT_ARR uint32_t* outShapes );
// ONE-SIDED smooth chain (ghost vertices avoid seam catching). Only the right-hand side of the travel direction is
// solid: points ordered left->right are solid from above only when ordered right->left. Returns the chain id (not a
// shape id); free with pb2_chain_destroy.
PB2_API uint32_t pb2_chain_create( uint32_t body, int32_t colliderIndex, int layer, PB2_IN_ARR const float* xy, int pointCount,
								   int isLoop, float friction, float restitution );
PB2_API void pb2_shape_destroy( uint32_t shape );
PB2_API void pb2_chain_destroy( uint32_t chain );
PB2_API void pb2_shape_set_material( uint32_t shape, float friction, float restitution );
PB2_API void pb2_shape_set_density( uint32_t shape, float density );


// ---- joints (see the record and the p[] table above) ---------------------------------------

// Creates a joint between two bodies, or between the world and bodyB when bodyA is 0. Returns 0 if a body is gone,
// both are the same body, or the type is unknown. A joint dies with either of its bodies; the id is then stale, and
// pb2_joint_is_valid says so.
PB2_API uint32_t pb2_joint_create( PB2_IN const PB2JointDef* def );
PB2_API void pb2_joint_destroy( uint32_t joint );
PB2_API int pb2_joint_is_valid( uint32_t joint );
// Re-applies everything tunable from the record (flags, thresholds, p[]) to a live joint and wakes its bodies. The type and
// the frames are fixed at creation: to move an anchor, destroy and create. Setting an unchanged value is harmless.
PB2_API void pb2_joint_apply( uint32_t joint, PB2_IN const PB2JointDef* def );
// out[0..7] = constraintForceX, constraintForceY, constraintTorque,
//             position (revolute: angle, prismatic / wheel: translation, distance: current length; else 0),
//             speed (prismatic: translation speed; else 0),
//             motorLoad (the motor's current force, or torque for revolute and wheel; else 0),
//             linearSeparation, angularSeparation (how far the constraint is currently violated)
PB2_API void pb2_joint_get_state( uint32_t joint, PB2_OUT_ARR float* out8 );

// ---- queries (layerMask: bit n set == layer n is hit) --------------------------------------

PB2_API int pb2_raycast( float ox, float oy, float dx, float dy, float maxDistance, uint32_t layerMask, int hitSensors,
						 PB2_OUT PB2RayHit* out );
// Returns the number written (<= capacity), sorted by distance.
PB2_API int pb2_raycast_all( float ox, float oy, float dx, float dy, float maxDistance, uint32_t layerMask, int hitSensors,
							 PB2_OUT_ARR PB2RayHit* out, int capacity );
// Each overlap writes collider indices (may contain duplicates for multi-shape colliders); returns count written.
PB2_API int pb2_overlap_point( float x, float y, uint32_t layerMask, int hitSensors, PB2_OUT_ARR int32_t* out, int capacity );
PB2_API int pb2_overlap_circle( float cx, float cy, float radius, uint32_t layerMask, int hitSensors, PB2_OUT_ARR int32_t* out,
								int capacity );
PB2_API int pb2_overlap_box( float cx, float cy, float halfW, float halfH, float angle, uint32_t layerMask, int hitSensors,
							 PB2_OUT_ARR int32_t* out, int capacity );

#ifdef __cplusplus
}
#endif

#endif
