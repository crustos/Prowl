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

#define PB2_ABI_VERSION 1
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
	int32_t reserved;
	intptr_t moves; // PB2BodyMove*      valid until the next pb2_step
	intptr_t contacts; // PB2ContactEvent*
	intptr_t sensors; // PB2SensorEvent*
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

// ---- lifecycle / ABI -----------------------------------------------------------------------

// Fills out[0..7] with sizeof() of the records above plus PB2_ABI_VERSION; managed code compares.
// out: [0]=version [1]=BodyMove [2]=ContactEvent [3]=SensorEvent [4]=StepInfo [5]=TransformSet [6]=RayHit [7]=sizeof(void*)
PB2_API void pb2_abi( int32_t* out8 );

PB2_API void pb2_world_create( float gravityX, float gravityY, int workerCount );
PB2_API void pb2_world_destroy( void );
PB2_API void pb2_world_set_gravity( float x, float y );
PB2_API void pb2_world_set_layer_matrix( const uint32_t* rows32 ); // rows[a] bit b == layers a,b collide

// Steps the world and gathers all events. One crossing per frame.
PB2_API void pb2_step( float dt, int subSteps, PB2StepInfo* info );

// Applies many teleports / kinematic moves in one crossing. dt is used by mode 1.
PB2_API void pb2_bodies_set_transforms( const PB2TransformSet* sets, int count, float dt );

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
PB2_API void pb2_body_get_state( uint32_t body, float* out10 );

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
PB2_API uint32_t pb2_shape_create_polygon( uint32_t body, int32_t colliderIndex, int layer, const float* xy, int pointCount,
										   float radius, float density, float friction, float restitution, uint32_t flags );
// Two-sided edges (what a Unity-style EdgeCollider2D means): one segment shape per edge, each carrying the
// collider index and layer. Writes (n-1) shape ids (n if isLoop) to outShapes and returns that count, 0 on bad
// input. The caller owns the ids and destroys each with pb2_shape_destroy. Segments have no ghost vertices, so a
// character sliding along a flat run of them can catch on the seams; use pb2_chain_create where that matters.
PB2_API int pb2_segments_create( uint32_t body, int32_t colliderIndex, int layer, const float* xy, int pointCount, int isLoop,
								 float friction, float restitution, uint32_t* outShapes );
// ONE-SIDED smooth chain (ghost vertices avoid seam catching). Only the right-hand side of the travel direction is
// solid: points ordered left->right are solid from above only when ordered right->left. Returns the chain id (not a
// shape id); free with pb2_chain_destroy.
PB2_API uint32_t pb2_chain_create( uint32_t body, int32_t colliderIndex, int layer, const float* xy, int pointCount,
								   int isLoop, float friction, float restitution );
PB2_API void pb2_shape_destroy( uint32_t shape );
PB2_API void pb2_chain_destroy( uint32_t chain );
PB2_API void pb2_shape_set_material( uint32_t shape, float friction, float restitution );
PB2_API void pb2_shape_set_density( uint32_t shape, float density );

// ---- queries (layerMask: bit n set == layer n is hit) --------------------------------------

PB2_API int pb2_raycast( float ox, float oy, float dx, float dy, float maxDistance, uint32_t layerMask, int hitSensors,
						 PB2RayHit* out );
// Returns the number written (<= capacity), sorted by distance.
PB2_API int pb2_raycast_all( float ox, float oy, float dx, float dy, float maxDistance, uint32_t layerMask, int hitSensors,
							 PB2RayHit* out, int capacity );
// Each overlap writes collider indices (may contain duplicates for multi-shape colliders); returns count written.
PB2_API int pb2_overlap_point( float x, float y, uint32_t layerMask, int hitSensors, int32_t* out, int capacity );
PB2_API int pb2_overlap_circle( float cx, float cy, float radius, uint32_t layerMask, int hitSensors, int32_t* out,
								int capacity );
PB2_API int pb2_overlap_box( float cx, float cy, float halfW, float halfH, float angle, uint32_t layerMask, int hitSensors,
							 int32_t* out, int capacity );

#ifdef __cplusplus
}
#endif

#endif
