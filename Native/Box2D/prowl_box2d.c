// See prowl_box2d.h for the design notes and ABI rules.

#include "prowl_box2d.h"

#include "box2d/box2d.h"
#include "box2d/collision.h"
#include "box2d/math_functions.h"

#include <float.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

// ---- state ---------------------------------------------------------------------------------

static b2WorldId g_world;
static int g_hasWorld;
static uint32_t g_matrix[PB2_LAYER_COUNT];

static PB2BodyMove* g_moves;
static int g_moveCap;
static PB2ContactEvent* g_contacts;
static int g_contactCap;
static PB2SensorEvent* g_sensors;
static int g_sensorCap;
static int32_t* g_jointEvents;
static int g_jointEventCap;

// A static body at the origin that world-anchored joints attach to (Box2D joints always join two bodies). It has no
// shapes and no managed index (userData 0), so nothing is ever reported about it. Created on first use.
static b2BodyId g_ground;
static int g_hasGround;

// ---- helpers -------------------------------------------------------------------------------

static inline uint32_t pack_id( const void* id )
{
	uint32_t v;
	memcpy( &v, id, 4 );
	return v;
}
#define UNPACK( T, v, name ) \
	T name;                  \
	memcpy( &name, &( v ), 4 )

static inline b2BodyId body_id( uint32_t v )
{
	UNPACK( b2BodyId, v, id );
	return id;
}
static inline b2ShapeId shape_id( uint32_t v )
{
	UNPACK( b2ShapeId, v, id );
	return id;
}
static inline b2ChainId chain_id( uint32_t v )
{
	UNPACK( b2ChainId, v, id );
	return id;
}
static inline b2JointId joint_id( uint32_t v )
{
	UNPACK( b2JointId, v, id );
	return id;
}

static void* grow( void* buf, int* cap, int need, size_t elem )
{
	if ( need <= *cap )
	{
		return buf;
	}
	int c = *cap ? *cap : 256;
	while ( c < need )
	{
		c *= 2;
	}
	void* p = realloc( buf, (size_t)c * elem );
	*cap = c;
	return p;
}

static inline void* pack_shape_user( int32_t colliderIndex, int layer )
{
	uint32_t v = ( (uint32_t)( layer & 31 ) << 24 ) | ( (uint32_t)( colliderIndex + 1 ) & 0xFFFFFFu );
	return (void*)(uintptr_t)v;
}
static inline uint32_t shape_user( b2ShapeId s )
{
	return (uint32_t)(uintptr_t)b2Shape_GetUserData( s );
}
static inline int32_t user_collider( uint32_t u )
{
	return (int32_t)( u & 0xFFFFFFu ) - 1;
}
static inline int user_layer( uint32_t u )
{
	return (int)( u >> 24 );
}
static inline int32_t shape_body_index( b2ShapeId s )
{
	return (int32_t)(intptr_t)b2Body_GetUserData( b2Shape_GetBody( s ) ) - 1;
}

static inline int layers_collide( uint32_t ua, uint32_t ub )
{
	return (int)( ( g_matrix[user_layer( ua )] >> user_layer( ub ) ) & 1u );
}

// Runs inside the broad phase (possibly on worker threads). Reads only; the matrix is written between steps.
static bool filter_callback( b2ShapeId a, b2ShapeId b, void* context )
{
	(void)context;
	return layers_collide( shape_user( a ), shape_user( b ) ) != 0;
}

// ---- ABI / lifecycle -----------------------------------------------------------------------

void pb2_abi( int32_t* out )
{
	out[0] = PB2_ABI_VERSION;
	out[1] = (int32_t)sizeof( PB2BodyMove );
	out[2] = (int32_t)sizeof( PB2ContactEvent );
	out[3] = (int32_t)sizeof( PB2SensorEvent );
	out[4] = (int32_t)sizeof( PB2StepInfo );
	out[5] = (int32_t)sizeof( PB2TransformSet );
	out[6] = (int32_t)sizeof( PB2RayHit );
	out[7] = (int32_t)sizeof( void* );
	out[8] = (int32_t)sizeof( PB2JointDef );
}

void pb2_world_create( float gx, float gy, int workerCount )
{
	if ( g_hasWorld )
	{
		b2DestroyWorld( g_world );
		g_hasWorld = 0;
	}
	g_hasGround = 0;
	for ( int i = 0; i < PB2_LAYER_COUNT; ++i )
	{
		g_matrix[i] = 0xFFFFFFFFu;
	}
	b2WorldDef def = b2DefaultWorldDef();
	def.gravity = (b2Vec2){ gx, gy };
	def.workerCount = workerCount < 1 ? 1 : workerCount;
	g_world = b2CreateWorld( &def );
	g_hasWorld = 1;
	b2World_SetCustomFilterCallback( g_world, filter_callback, NULL );
}

void pb2_world_destroy( void )
{
	if ( g_hasWorld )
	{
		b2DestroyWorld( g_world );
		g_hasWorld = 0;
	}
	g_hasGround = 0;
	free( g_moves );
	free( g_contacts );
	free( g_sensors );
	free( g_jointEvents );
	g_moves = NULL;
	g_contacts = NULL;
	g_sensors = NULL;
	g_jointEvents = NULL;
	g_moveCap = g_contactCap = g_sensorCap = g_jointEventCap = 0;
}

void pb2_world_set_gravity( float x, float y )
{
	b2World_SetGravity( g_world, (b2Vec2){ x, y } );
}

void pb2_world_set_layer_matrix( const uint32_t* rows )
{
	memcpy( g_matrix, rows, sizeof( g_matrix ) );
}

// ---- stepping ------------------------------------------------------------------------------

void pb2_step( float dt, int subSteps, PB2StepInfo* info )
{
	b2World_Step( g_world, dt, subSteps < 1 ? 1 : subSteps );

	// body moves
	b2BodyEvents be = b2World_GetBodyEvents( g_world );
	g_moves = (PB2BodyMove*)grow( g_moves, &g_moveCap, be.moveCount, sizeof( PB2BodyMove ) );
	int moves = 0;
	for ( int i = 0; i < be.moveCount; ++i )
	{
		const b2BodyMoveEvent* e = &be.moveEvents[i];
		int32_t idx = (int32_t)(intptr_t)e->userData - 1;
		if ( idx < 0 )
		{
			continue; // body not owned by managed code
		}
		PB2BodyMove* m = &g_moves[moves++];
		m->bodyIndex = idx;
		m->x = e->transform.p.x;
		m->y = e->transform.p.y;
		m->c = e->transform.q.c;
		m->s = e->transform.q.s;
		m->fellAsleep = e->fellAsleep ? 1 : 0;
	}

	// contacts: begin events (with manifold resolved here, not across the boundary), then end events
	b2ContactEvents ce = b2World_GetContactEvents( g_world );
	g_contacts = (PB2ContactEvent*)grow( g_contacts, &g_contactCap, ce.beginCount + ce.endCount, sizeof( PB2ContactEvent ) );
	int contacts = 0;
	for ( int i = 0; i < ce.beginCount; ++i )
	{
		const b2ContactBeginTouchEvent* e = &ce.beginEvents[i];
		if ( !b2Shape_IsValid( e->shapeIdA ) || !b2Shape_IsValid( e->shapeIdB ) )
		{
			continue;
		}
		uint32_t ua = shape_user( e->shapeIdA ), ub = shape_user( e->shapeIdB );
		PB2ContactEvent* c = &g_contacts[contacts++];
		memset( c, 0, sizeof( *c ) );
		c->colliderA = user_collider( ua );
		c->colliderB = user_collider( ub );
		c->bodyA = shape_body_index( e->shapeIdA );
		c->bodyB = shape_body_index( e->shapeIdB );
		c->flags = PB2_EVENT_BEGIN;

		b2ContactData cd = b2Contact_GetData( e->contactId );
		if ( cd.manifold.pointCount > 0 )
		{
			b2Pos origin = b2Body_GetPosition( b2Shape_GetBody( e->shapeIdA ) );
			c->px = origin.x + cd.manifold.points[0].anchorA.x;
			c->py = origin.y + cd.manifold.points[0].anchorA.y;
			c->nx = cd.manifold.normal.x;
			c->ny = cd.manifold.normal.y;
			float impulse = 0.0f;
			for ( int p = 0; p < cd.manifold.pointCount; ++p )
			{
				impulse += cd.manifold.points[p].totalNormalImpulse;
			}
			c->impulse = impulse;
		}
	}
	int beginCount = contacts;
	for ( int i = 0; i < ce.endCount; ++i )
	{
		const b2ContactEndTouchEvent* e = &ce.endEvents[i];
		if ( !b2Shape_IsValid( e->shapeIdA ) || !b2Shape_IsValid( e->shapeIdB ) )
		{
			continue; // a destroyed shape has already been cleaned up by managed code
		}
		uint32_t ua = shape_user( e->shapeIdA ), ub = shape_user( e->shapeIdB );
		PB2ContactEvent* c = &g_contacts[contacts++];
		memset( c, 0, sizeof( *c ) );
		c->colliderA = user_collider( ua );
		c->colliderB = user_collider( ub );
		c->bodyA = shape_body_index( e->shapeIdA );
		c->bodyB = shape_body_index( e->shapeIdB );
		c->flags = PB2_EVENT_END;
	}

	// sensors. Sensors ignore the custom filter, so the layer matrix is applied here instead.
	b2SensorEvents se = b2World_GetSensorEvents( g_world );
	g_sensors = (PB2SensorEvent*)grow( g_sensors, &g_sensorCap, se.beginCount + se.endCount, sizeof( PB2SensorEvent ) );
	int sensors = 0;
	for ( int i = 0; i < se.beginCount; ++i )
	{
		b2ShapeId s = se.beginEvents[i].sensorShapeId, v = se.beginEvents[i].visitorShapeId;
		if ( !b2Shape_IsValid( s ) || !b2Shape_IsValid( v ) )
		{
			continue;
		}
		uint32_t us = shape_user( s ), uv = shape_user( v );
		if ( !layers_collide( us, uv ) )
		{
			continue;
		}
		g_sensors[sensors++] = (PB2SensorEvent){ user_collider( us ), user_collider( uv ), PB2_EVENT_BEGIN };
	}
	for ( int i = 0; i < se.endCount; ++i )
	{
		b2ShapeId s = se.endEvents[i].sensorShapeId, v = se.endEvents[i].visitorShapeId;
		if ( !b2Shape_IsValid( s ) || !b2Shape_IsValid( v ) )
		{
			continue;
		}
		uint32_t us = shape_user( s ), uv = shape_user( v );
		if ( !layers_collide( us, uv ) )
		{
			continue;
		}
		g_sensors[sensors++] = (PB2SensorEvent){ user_collider( us ), user_collider( uv ), PB2_EVENT_END };
	}

	// joints over their force / torque threshold. Copied out now: the array is invalid once a joint is destroyed, and
	// managed code destroys a broken joint as soon as it hears about it.
	b2JointEvents je = b2World_GetJointEvents( g_world );
	g_jointEvents = (int32_t*)grow( g_jointEvents, &g_jointEventCap, je.count, sizeof( int32_t ) );
	int jointEvents = 0;
	for ( int i = 0; i < je.count; ++i )
	{
		int32_t idx = (int32_t)(intptr_t)je.jointEvents[i].userData - 1;
		if ( idx >= 0 )
		{
			g_jointEvents[jointEvents++] = idx;
		}
	}

	info->moveCount = moves;
	info->contactCount = contacts;
	info->contactBeginCount = beginCount;
	info->sensorCount = sensors;
	info->awakeBodyCount = b2World_GetAwakeBodyCount( g_world );
	info->jointEventCount = jointEvents;
	info->moves = (intptr_t)g_moves;
	info->contacts = (intptr_t)g_contacts;
	info->sensors = (intptr_t)g_sensors;
	info->joints = (intptr_t)g_jointEvents;
}

// ---- waking ---------------------------------------------------------------------------------
//
// Box2D does not wake a sleeping body when a static body is moved out from under it or into it, or when its motion locks
// change. A platform repositioned through a Transform would leave bodies hanging in mid-air, so the shim does it.

typedef struct WakeCtx
{
	uint32_t* ids;
	int count, cap;
} WakeCtx;

static bool wake_collect( b2ShapeId shape, void* context )
{
	WakeCtx* c = (WakeCtx*)context;
	b2BodyId body = b2Shape_GetBody( shape );
	if ( b2Body_GetType( body ) == b2_staticBody )
	{
		return true;
	}
	if ( c->count == c->cap )
	{
		c->cap = c->cap ? c->cap * 2 : 64;
		c->ids = (uint32_t*)realloc( c->ids, sizeof( uint32_t ) * (size_t)c->cap );
	}
	c->ids[c->count++] = pack_id( &body );
	return true;
}

// Collects first and wakes afterwards: the world must not be modified from inside a query callback.
static void wake_in_aabb( b2AABB box )
{
	WakeCtx ctx = { NULL, 0, 0 };
	b2World_OverlapAABB( g_world, (b2Pos){ 0.0f, 0.0f }, box, b2DefaultQueryFilter(), wake_collect, &ctx );
	for ( int i = 0; i < ctx.count; ++i )
	{
		b2BodyId id = body_id( ctx.ids[i] );
		if ( b2Body_IsValid( id ) )
		{
			b2Body_SetAwake( id, true );
		}
	}
	free( ctx.ids );
}

// Teleports a body. A static body that moves wakes whatever was touching it, and whatever it now overlaps. Any other body is woken itself:
// b2Body_SetTransform moves a sleeping body but leaves it asleep, so it would hang where it was put with no gravity until something else
// woke it (Rigidbody2D.Teleport wakes explicitly; the batched path used for Transform edits did not).
static void set_transform_waking( b2BodyId id, b2Pos p, b2Rot q )
{
	bool isStatic = b2Body_GetType( id ) == b2_staticBody;
	bool hasShapes = b2Body_GetShapeCount( id ) > 0;
	if ( isStatic && hasShapes )
	{
		wake_in_aabb( b2Body_ComputeAABB( id ) );
	}
	b2Body_SetTransform( id, p, q );
	if ( isStatic && hasShapes )
	{
		wake_in_aabb( b2Body_ComputeAABB( id ) );
	}
	if ( !isStatic )
	{
		b2Body_SetAwake( id, true );
	}
}

void pb2_bodies_set_transforms( const PB2TransformSet* sets, int count, float dt )
{
	for ( int i = 0; i < count; ++i )
	{
		const PB2TransformSet* t = &sets[i];
		b2BodyId id = body_id( t->body );
		b2Pos p = { t->x, t->y };
		b2Rot q = b2MakeRot( t->angle );
		if ( t->mode == 1 )
		{
			b2WorldTransform target = { p, q };
			b2Body_SetTargetTransform( id, target, dt, true );
		}
		else
		{
			set_transform_waking( id, p, q );
		}
	}
}

// ---- bodies --------------------------------------------------------------------------------

static void apply_flags( b2BodyId id, uint32_t flags )
{
	b2Body_SetBullet( id, ( flags & PB2_BF_BULLET ) != 0 );
	b2Body_EnableSleep( id, ( flags & PB2_BF_NO_SLEEP ) == 0 );
	b2MotionLocks locks = { 0 };
	locks.linearX = ( flags & PB2_BF_LOCK_X ) != 0;
	locks.linearY = ( flags & PB2_BF_LOCK_Y ) != 0;
	locks.angularZ = ( flags & PB2_BF_LOCK_ROT ) != 0;
	b2Body_SetMotionLocks( id, locks );
	bool wantEnabled = ( flags & PB2_BF_DISABLED ) == 0;
	if ( wantEnabled != b2Body_IsEnabled( id ) )
	{
		if ( wantEnabled )
		{
			b2Body_Enable( id );
		}
		else
		{
			b2Body_Disable( id );
		}
	}
}

uint32_t pb2_body_create( int type, float x, float y, float angle, int32_t bodyIndex, float gravityScale, float linearDamping,
						  float angularDamping, uint32_t flags )
{
	b2BodyDef def = b2DefaultBodyDef();
	def.type = (b2BodyType)type;
	def.position = (b2Pos){ x, y };
	def.rotation = b2MakeRot( angle );
	def.userData = (void*)(intptr_t)( bodyIndex + 1 );
	def.gravityScale = gravityScale;
	def.linearDamping = linearDamping;
	def.angularDamping = angularDamping;
	def.isBullet = ( flags & PB2_BF_BULLET ) != 0;
	def.enableSleep = ( flags & PB2_BF_NO_SLEEP ) == 0;
	def.isEnabled = ( flags & PB2_BF_DISABLED ) == 0;
	def.motionLocks.linearX = ( flags & PB2_BF_LOCK_X ) != 0;
	def.motionLocks.linearY = ( flags & PB2_BF_LOCK_Y ) != 0;
	def.motionLocks.angularZ = ( flags & PB2_BF_LOCK_ROT ) != 0;
	b2BodyId id = b2CreateBody( g_world, &def );
	return pack_id( &id );
}

void pb2_body_destroy( uint32_t body )
{
	b2BodyId id = body_id( body );
	if ( b2Body_IsValid( id ) )
	{
		b2DestroyBody( id );
	}
}

void pb2_body_set_transform( uint32_t body, float x, float y, float angle )
{
	set_transform_waking( body_id( body ), (b2Pos){ x, y }, b2MakeRot( angle ) );
}
void pb2_body_set_type( uint32_t body, int type )
{
	b2Body_SetType( body_id( body ), (b2BodyType)type );
}
void pb2_body_set_flags( uint32_t body, uint32_t flags )
{
	b2BodyId id = body_id( body );
	apply_flags( id, flags );
	// Lifting a lock (or any change here) on a sleeping body has to get it moving again.
	if ( b2Body_GetType( id ) != b2_staticBody && b2Body_IsEnabled( id ) )
	{
		b2Body_SetAwake( id, true );
	}
}
void pb2_body_set_damping( uint32_t body, float linear, float angular )
{
	b2Body_SetLinearDamping( body_id( body ), linear );
	b2Body_SetAngularDamping( body_id( body ), angular );
}
void pb2_body_set_gravity_scale( uint32_t body, float scale )
{
	b2Body_SetGravityScale( body_id( body ), scale );
}
void pb2_body_set_velocity( uint32_t body, float vx, float vy, float w )
{
	b2Body_SetLinearVelocity( body_id( body ), (b2Vec2){ vx, vy } );
	b2Body_SetAngularVelocity( body_id( body ), w );
}
void pb2_body_set_mass( uint32_t body, float mass )
{
	b2BodyId id = body_id( body );
	b2MassData md = b2Body_GetMassData( id );
	if ( md.mass > 0.0f && mass > 0.0f )
	{
		md.rotationalInertia *= mass / md.mass;
		md.mass = mass;
		b2Body_SetMassData( id, md );
	}
}
void pb2_body_set_awake( uint32_t body, int awake )
{
	b2Body_SetAwake( body_id( body ), awake != 0 );
}
void pb2_body_apply_force( uint32_t body, float fx, float fy, int hasPoint, float px, float py )
{
	b2BodyId id = body_id( body );
	if ( hasPoint )
	{
		b2Body_ApplyForce( id, (b2Vec2){ fx, fy }, (b2Pos){ px, py }, true );
	}
	else
	{
		b2Body_ApplyForceToCenter( id, (b2Vec2){ fx, fy }, true );
	}
}
void pb2_body_apply_impulse( uint32_t body, float ix, float iy, int hasPoint, float px, float py )
{
	b2BodyId id = body_id( body );
	if ( hasPoint )
	{
		b2Body_ApplyLinearImpulse( id, (b2Vec2){ ix, iy }, (b2Pos){ px, py }, true );
	}
	else
	{
		b2Body_ApplyLinearImpulseToCenter( id, (b2Vec2){ ix, iy }, true );
	}
}
void pb2_body_apply_torque( uint32_t body, float torque, int asImpulse )
{
	if ( asImpulse )
	{
		b2Body_ApplyAngularImpulse( body_id( body ), torque, true );
	}
	else
	{
		b2Body_ApplyTorque( body_id( body ), torque, true );
	}
}
void pb2_body_get_state( uint32_t body, float* out )
{
	b2BodyId id = body_id( body );
	b2WorldTransform xf = b2Body_GetTransform( id );
	b2Vec2 v = b2Body_GetLinearVelocity( id );
	out[0] = xf.p.x;
	out[1] = xf.p.y;
	out[2] = xf.q.c;
	out[3] = xf.q.s;
	out[4] = v.x;
	out[5] = v.y;
	out[6] = b2Body_GetAngularVelocity( id );
	out[7] = b2Body_GetMass( id );
	out[8] = b2Body_GetRotationalInertia( id );
	out[9] = b2Body_IsAwake( id ) ? 1.0f : 0.0f;
}

// ---- shapes --------------------------------------------------------------------------------

static b2ShapeDef make_shape_def( int32_t colliderIndex, int layer, float density, float friction, float restitution,
								  uint32_t flags )
{
	b2ShapeDef def = b2DefaultShapeDef();
	def.userData = pack_shape_user( colliderIndex, layer );
	def.density = density;
	def.material.friction = friction;
	def.material.restitution = restitution;
	def.isSensor = ( flags & PB2_SF_SENSOR ) != 0;
	def.enableSensorEvents = true; // lets sensors see this shape (ignored on sensors themselves)
	def.enableContactEvents = !def.isSensor;
	def.enableCustomFiltering = true; // layer matrix, see filter_callback
	def.updateBodyMass = true;
	return def;
}

uint32_t pb2_shape_create_circle( uint32_t body, int32_t ci, int layer, float cx, float cy, float radius, float density,
								  float friction, float restitution, uint32_t flags )
{
	b2ShapeDef def = make_shape_def( ci, layer, density, friction, restitution, flags );
	b2Circle c = { { cx, cy }, radius };
	b2ShapeId id = b2CreateCircleShape( body_id( body ), &def, &c );
	return pack_id( &id );
}

uint32_t pb2_shape_create_box( uint32_t body, int32_t ci, int layer, float hw, float hh, float ox, float oy, float angle,
							   float cornerRadius, float density, float friction, float restitution, uint32_t flags )
{
	b2ShapeDef def = make_shape_def( ci, layer, density, friction, restitution, flags );
	b2Polygon poly = b2MakeOffsetBox( hw, hh, (b2Vec2){ ox, oy }, b2MakeRot( angle ) );
	poly.radius = cornerRadius;
	b2ShapeId id = b2CreatePolygonShape( body_id( body ), &def, &poly );
	return pack_id( &id );
}

uint32_t pb2_shape_create_capsule( uint32_t body, int32_t ci, int layer, float x1, float y1, float x2, float y2, float radius,
								   float density, float friction, float restitution, uint32_t flags )
{
	b2ShapeDef def = make_shape_def( ci, layer, density, friction, restitution, flags );
	b2Capsule cap = { { x1, y1 }, { x2, y2 }, radius };
	b2ShapeId id = b2CreateCapsuleShape( body_id( body ), &def, &cap );
	return pack_id( &id );
}

uint32_t pb2_shape_create_polygon( uint32_t body, int32_t ci, int layer, const float* xy, int count, float radius,
								   float density, float friction, float restitution, uint32_t flags )
{
	if ( count < 3 || count > B2_MAX_POLYGON_VERTICES )
	{
		return 0;
	}
	b2Vec2 pts[B2_MAX_POLYGON_VERTICES];
	for ( int i = 0; i < count; ++i )
	{
		pts[i] = (b2Vec2){ xy[2 * i], xy[2 * i + 1] };
	}
	b2Hull hull = b2ComputeHull( pts, count );
	if ( hull.count == 0 )
	{
		return 0;
	}
	b2ShapeDef def = make_shape_def( ci, layer, density, friction, restitution, flags );
	b2Polygon poly = b2MakePolygon( &hull, radius );
	b2ShapeId id = b2CreatePolygonShape( body_id( body ), &def, &poly );
	return pack_id( &id );
}

int pb2_segments_create( uint32_t body, int32_t ci, int layer, const float* xy, int count, int isLoop, float friction,
						 float restitution, uint32_t* outShapes )
{
	if ( count < 2 || ( isLoop && count < 3 ) )
	{
		return 0;
	}
	int segments = isLoop ? count : count - 1;
	b2ShapeDef def = make_shape_def( ci, layer, 0.0f, friction, restitution, 0 );
	for ( int i = 0; i < segments; ++i )
	{
		int j = ( i + 1 ) % count;
		b2Segment seg = { { xy[2 * i], xy[2 * i + 1] }, { xy[2 * j], xy[2 * j + 1] } };
		b2ShapeId id = b2CreateSegmentShape( body_id( body ), &def, &seg );
		outShapes[i] = pack_id( &id );
	}
	return segments;
}

uint32_t pb2_chain_create( uint32_t body, int32_t ci, int layer, const float* xy, int count, int isLoop, float friction,
						   float restitution )
{
	if ( count < ( isLoop ? 3 : 2 ) ) // open chains get ghost points synthesised from their end segments
	{
		return 0;
	}
	b2Vec2* pts = (b2Vec2*)malloc( sizeof( b2Vec2 ) * (size_t)count );
	for ( int i = 0; i < count; ++i )
	{
		pts[i] = (b2Vec2){ xy[2 * i], xy[2 * i + 1] };
	}
	b2SurfaceMaterial mat = b2DefaultShapeDef().material;
	mat.friction = friction;
	mat.restitution = restitution;

	b2ChainDef def = b2DefaultChainDef();
	def.userData = pack_shape_user( ci, layer );
	def.points = pts;
	def.pointCount = count;
	def.materials = &mat;
	def.materialCount = 1;
	def.isLoop = isLoop != 0;
	def.enableSensorEvents = true;
	if ( !isLoop )
	{
		def.ghost1 = b2Add( pts[0], b2Sub( pts[0], pts[1] ) );
		def.ghost2 = b2Add( pts[count - 1], b2Sub( pts[count - 1], pts[count - 2] ) );
	}
	b2ChainId id = b2CreateChain( body_id( body ), &def );
	free( pts ); // points are cloned by Box2D
	return pack_id( &id );
}

void pb2_shape_destroy( uint32_t shape )
{
	b2ShapeId id = shape_id( shape );
	if ( b2Shape_IsValid( id ) )
	{
		b2DestroyShape( id, true );
	}
}
void pb2_chain_destroy( uint32_t chain )
{
	b2DestroyChain( chain_id( chain ) );
}
void pb2_shape_set_material( uint32_t shape, float friction, float restitution )
{
	b2Shape_SetFriction( shape_id( shape ), friction );
	b2Shape_SetRestitution( shape_id( shape ), restitution );
}
void pb2_shape_set_density( uint32_t shape, float density )
{
	b2Shape_SetDensity( shape_id( shape ), density, true );
}

// ---- joints --------------------------------------------------------------------------------

static b2BodyId ground_body( void )
{
	if ( !g_hasGround )
	{
		b2BodyDef def = b2DefaultBodyDef();
		def.type = b2_staticBody;
		def.userData = NULL;
		g_ground = b2CreateBody( g_world, &def );
		g_hasGround = 1;
	}
	return g_ground;
}

// Managed code passes "never" as FLT_MAX or infinity; anything that is not a finite non-negative number means the same.
static float clean_threshold( float t )
{
	return ( t >= 0.0f && t < FLT_MAX ) ? t : FLT_MAX;
}

// Writes every tunable value of the record onto a live joint. Box2D's setters leave an unchanged value alone (they only
// reset accumulated impulses when something is switched on or off), so calling this with the same record is harmless.
static void apply_joint( b2JointId id, const PB2JointDef* d )
{
	const float* p = d->p;
	bool spring = ( d->flags & PB2_JF_SPRING ) != 0;
	bool limit = ( d->flags & PB2_JF_LIMIT ) != 0;
	bool motor = ( d->flags & PB2_JF_MOTOR ) != 0;

	b2Joint_SetCollideConnected( id, ( d->flags & PB2_JF_COLLIDE_CONNECTED ) != 0 );
	b2Joint_SetForceThreshold( id, clean_threshold( d->forceThreshold ) );
	b2Joint_SetTorqueThreshold( id, clean_threshold( d->torqueThreshold ) );

	switch ( d->type )
	{
		case PB2_JOINT_DISTANCE:
			b2DistanceJoint_SetLength( id, p[0] );
			b2DistanceJoint_SetLengthRange( id, p[1], p[2] );
			b2DistanceJoint_EnableSpring( id, spring );
			b2DistanceJoint_SetSpringHertz( id, p[3] );
			b2DistanceJoint_SetSpringDampingRatio( id, p[4] );
			b2DistanceJoint_EnableLimit( id, limit );
			b2DistanceJoint_EnableMotor( id, motor );
			b2DistanceJoint_SetMaxMotorForce( id, p[5] );
			b2DistanceJoint_SetMotorSpeed( id, p[6] );
			break;

		case PB2_JOINT_REVOLUTE:
			b2RevoluteJoint_SetTargetAngle( id, p[0] );
			b2RevoluteJoint_EnableSpring( id, spring );
			b2RevoluteJoint_SetSpringHertz( id, p[1] );
			b2RevoluteJoint_SetSpringDampingRatio( id, p[2] );
			b2RevoluteJoint_SetLimits( id, p[3], p[4] );
			b2RevoluteJoint_EnableLimit( id, limit );
			b2RevoluteJoint_EnableMotor( id, motor );
			b2RevoluteJoint_SetMaxMotorTorque( id, p[5] );
			b2RevoluteJoint_SetMotorSpeed( id, p[6] );
			break;

		case PB2_JOINT_PRISMATIC:
			b2PrismaticJoint_EnableSpring( id, spring );
			b2PrismaticJoint_SetSpringHertz( id, p[0] );
			b2PrismaticJoint_SetSpringDampingRatio( id, p[1] );
			b2PrismaticJoint_SetTargetTranslation( id, p[2] );
			b2PrismaticJoint_SetLimits( id, p[3], p[4] );
			b2PrismaticJoint_EnableLimit( id, limit );
			b2PrismaticJoint_EnableMotor( id, motor );
			b2PrismaticJoint_SetMaxMotorForce( id, p[5] );
			b2PrismaticJoint_SetMotorSpeed( id, p[6] );
			break;

		case PB2_JOINT_WHEEL:
			b2WheelJoint_EnableSpring( id, spring );
			b2WheelJoint_SetSpringHertz( id, p[0] );
			b2WheelJoint_SetSpringDampingRatio( id, p[1] );
			b2WheelJoint_SetLimits( id, p[2], p[3] );
			b2WheelJoint_EnableLimit( id, limit );
			b2WheelJoint_EnableMotor( id, motor );
			b2WheelJoint_SetMaxMotorTorque( id, p[4] );
			b2WheelJoint_SetMotorSpeed( id, p[5] );
			break;

		case PB2_JOINT_WELD:
			b2WeldJoint_SetLinearHertz( id, p[0] );
			b2WeldJoint_SetAngularHertz( id, p[1] );
			b2WeldJoint_SetLinearDampingRatio( id, p[2] );
			b2WeldJoint_SetAngularDampingRatio( id, p[3] );
			break;

		case PB2_JOINT_MOTOR:
			b2MotorJoint_SetLinearVelocity( id, (b2Vec2){ p[0], p[1] } );
			b2MotorJoint_SetMaxVelocityForce( id, p[2] );
			b2MotorJoint_SetAngularVelocity( id, p[3] );
			b2MotorJoint_SetMaxVelocityTorque( id, p[4] );
			b2MotorJoint_SetLinearHertz( id, p[5] );
			b2MotorJoint_SetLinearDampingRatio( id, p[6] );
			b2MotorJoint_SetMaxSpringForce( id, p[7] );
			b2MotorJoint_SetAngularHertz( id, p[8] );
			b2MotorJoint_SetAngularDampingRatio( id, p[9] );
			b2MotorJoint_SetMaxSpringTorque( id, p[10] );
			break;

		default: // PB2_JOINT_FILTER has nothing to tune
			break;
	}
}

static void fill_joint_base( b2JointDef* base, const PB2JointDef* d, b2BodyId a, b2BodyId b )
{
	base->userData = (void*)(intptr_t)( d->jointIndex + 1 );
	base->bodyIdA = a;
	base->bodyIdB = b;
	base->localFrameA = (b2Transform){ { d->ax, d->ay }, b2MakeRot( d->aAngle ) };
	base->localFrameB = (b2Transform){ { d->bx, d->by }, b2MakeRot( d->bAngle ) };
	base->forceThreshold = clean_threshold( d->forceThreshold );
	base->torqueThreshold = clean_threshold( d->torqueThreshold );
	base->collideConnected = ( d->flags & PB2_JF_COLLIDE_CONNECTED ) != 0;
}

uint32_t pb2_joint_create( const PB2JointDef* d )
{
	b2BodyId b = body_id( d->bodyB );
	if ( !b2Body_IsValid( b ) )
	{
		return 0;
	}
	b2BodyId a;
	if ( d->bodyA != 0 )
	{
		a = body_id( d->bodyA );
		if ( !b2Body_IsValid( a ) )
		{
			return 0;
		}
	}
	else
	{
		a = ground_body();
	}
	if ( pack_id( &a ) == pack_id( &b ) )
	{
		return 0;
	}

	b2JointId id;
	switch ( d->type )
	{
		case PB2_JOINT_DISTANCE:
		{
			b2DistanceJointDef def = b2DefaultDistanceJointDef();
			fill_joint_base( &def.base, d, a, b );
			def.length = d->p[0];
			def.minLength = d->p[1];
			def.maxLength = d->p[2];
			id = b2CreateDistanceJoint( g_world, &def );
			break;
		}
		case PB2_JOINT_REVOLUTE:
		{
			b2RevoluteJointDef def = b2DefaultRevoluteJointDef();
			fill_joint_base( &def.base, d, a, b );
			id = b2CreateRevoluteJoint( g_world, &def );
			break;
		}
		case PB2_JOINT_PRISMATIC:
		{
			b2PrismaticJointDef def = b2DefaultPrismaticJointDef();
			fill_joint_base( &def.base, d, a, b );
			id = b2CreatePrismaticJoint( g_world, &def );
			break;
		}
		case PB2_JOINT_WHEEL:
		{
			b2WheelJointDef def = b2DefaultWheelJointDef();
			fill_joint_base( &def.base, d, a, b );
			id = b2CreateWheelJoint( g_world, &def );
			break;
		}
		case PB2_JOINT_WELD:
		{
			b2WeldJointDef def = b2DefaultWeldJointDef();
			fill_joint_base( &def.base, d, a, b );
			id = b2CreateWeldJoint( g_world, &def );
			break;
		}
		case PB2_JOINT_MOTOR:
		{
			b2MotorJointDef def = b2DefaultMotorJointDef();
			fill_joint_base( &def.base, d, a, b );
			id = b2CreateMotorJoint( g_world, &def );
			break;
		}
		case PB2_JOINT_FILTER:
		{
			b2FilterJointDef def = b2DefaultFilterJointDef();
			fill_joint_base( &def.base, d, a, b );
			id = b2CreateFilterJoint( g_world, &def );
			break;
		}
		default:
			return 0;
	}

	// The definition structs only carry the type's headline values; everything else goes through the setters so that
	// creating and tuning a joint share one code path.
	apply_joint( id, d );
	return pack_id( &id );
}

void pb2_joint_destroy( uint32_t joint )
{
	b2JointId id = joint_id( joint );
	if ( b2Joint_IsValid( id ) )
	{
		b2DestroyJoint( id );
	}
}

int pb2_joint_is_valid( uint32_t joint )
{
	return joint != 0 && b2Joint_IsValid( joint_id( joint ) ) ? 1 : 0;
}

void pb2_joint_apply( uint32_t joint, const PB2JointDef* d )
{
	b2JointId id = joint_id( joint );
	if ( joint == 0 || !b2Joint_IsValid( id ) )
	{
		return;
	}
	apply_joint( id, d );
	// The setters do not wake anything, so a motor switched on under a sleeping body would do nothing.
	b2Joint_WakeBodies( id );
}

void pb2_joint_get_state( uint32_t joint, float* out )
{
	memset( out, 0, sizeof( float ) * 8 );
	b2JointId id = joint_id( joint );
	if ( joint == 0 || !b2Joint_IsValid( id ) )
	{
		return;
	}

	b2Vec2 force = b2Joint_GetConstraintForce( id );
	out[0] = force.x;
	out[1] = force.y;
	out[2] = b2Joint_GetConstraintTorque( id );
	out[6] = b2Joint_GetLinearSeparation( id );
	out[7] = b2Joint_GetAngularSeparation( id );

	switch ( b2Joint_GetType( id ) )
	{
		case b2_distanceJoint:
			out[3] = b2DistanceJoint_GetCurrentLength( id );
			out[5] = b2DistanceJoint_GetMotorForce( id );
			break;
		case b2_revoluteJoint:
			out[3] = b2RevoluteJoint_GetAngle( id );
			out[5] = b2RevoluteJoint_GetMotorTorque( id );
			break;
		case b2_prismaticJoint:
			out[3] = b2PrismaticJoint_GetTranslation( id );
			out[4] = b2PrismaticJoint_GetSpeed( id );
			out[5] = b2PrismaticJoint_GetMotorForce( id );
			break;
		case b2_wheelJoint:
		{
			// Box2D has no wheel translation getter; project the anchor separation onto the axis (x of frame A).
			b2BodyId bodyA = b2Joint_GetBodyA( id ), bodyB = b2Joint_GetBodyB( id );
			b2Transform fa = b2Joint_GetLocalFrameA( id ), fb = b2Joint_GetLocalFrameB( id );
			b2Rot qA = b2Body_GetRotation( bodyA );
			b2Pos pA = b2Body_GetPosition( bodyA ), pB = b2Body_GetPosition( bodyB );
			b2Vec2 anchorA = b2RotateVector( qA, fa.p );
			b2Vec2 anchorB = b2RotateVector( b2Body_GetRotation( bodyB ), fb.p );
			b2Vec2 d = { ( pB.x + anchorB.x ) - ( pA.x + anchorA.x ), ( pB.y + anchorB.y ) - ( pA.y + anchorA.y ) };
			b2Vec2 axis = b2RotateVector( b2MulRot( qA, fa.q ), (b2Vec2){ 1.0f, 0.0f } );
			out[3] = d.x * axis.x + d.y * axis.y;
			out[5] = b2WheelJoint_GetMotorTorque( id );
			break;
		}
		default:
			break;
	}
}

// ---- queries -------------------------------------------------------------------------------

typedef struct RayCtx
{
	uint32_t mask;
	int hitSensors;
	int all;
	PB2RayHit* out;
	int capacity;
	int count;
	PB2RayHit best;
	int hasBest;
} RayCtx;

static float ray_callback( b2ShapeId shape, b2Pos point, b2Vec2 normal, float fraction, void* context )
{
	RayCtx* c = (RayCtx*)context;
	uint32_t u = shape_user( shape );
	if ( !( ( c->mask >> user_layer( u ) ) & 1u ) )
	{
		return -1.0f; // ignore this shape, keep going
	}
	if ( !c->hitSensors && b2Shape_IsSensor( shape ) )
	{
		return -1.0f;
	}
	PB2RayHit h;
	h.collider = user_collider( u );
	h.body = shape_body_index( shape );
	h.px = point.x;
	h.py = point.y;
	h.nx = normal.x;
	h.ny = normal.y;
	h.fraction = fraction;
	if ( c->all )
	{
		if ( c->count < c->capacity )
		{
			c->out[c->count++] = h;
		}
		return 1.0f; // do not clip: gather everything along the ray
	}
	c->best = h;
	c->hasBest = 1;
	return fraction; // clip: only closer hits from here on
}

static int ray_setup( float ox, float oy, float dx, float dy, float maxDistance, b2Vec2* translation )
{
	float len = sqrtf( dx * dx + dy * dy );
	if ( len < 1e-12f || maxDistance <= 0.0f )
	{
		return 0;
	}
	float s = maxDistance / len;
	(void)ox;
	(void)oy;
	*translation = (b2Vec2){ dx * s, dy * s };
	return 1;
}

int pb2_raycast( float ox, float oy, float dx, float dy, float maxDistance, uint32_t layerMask, int hitSensors, PB2RayHit* out )
{
	b2Vec2 t;
	if ( !ray_setup( ox, oy, dx, dy, maxDistance, &t ) )
	{
		return 0;
	}
	RayCtx c;
	memset( &c, 0, sizeof( c ) );
	c.mask = layerMask;
	c.hitSensors = hitSensors;
	b2World_CastRay( g_world, (b2Pos){ ox, oy }, t, b2DefaultQueryFilter(), ray_callback, &c );
	if ( c.hasBest )
	{
		*out = c.best;
		return 1;
	}
	return 0;
}

static int ray_cmp( const void* a, const void* b )
{
	float fa = ( (const PB2RayHit*)a )->fraction, fb = ( (const PB2RayHit*)b )->fraction;
	return fa < fb ? -1 : ( fa > fb ? 1 : 0 );
}

int pb2_raycast_all( float ox, float oy, float dx, float dy, float maxDistance, uint32_t layerMask, int hitSensors,
					 PB2RayHit* out, int capacity )
{
	b2Vec2 t;
	if ( !ray_setup( ox, oy, dx, dy, maxDistance, &t ) )
	{
		return 0;
	}
	RayCtx c;
	memset( &c, 0, sizeof( c ) );
	c.mask = layerMask;
	c.hitSensors = hitSensors;
	c.all = 1;
	c.out = out;
	c.capacity = capacity;
	b2World_CastRay( g_world, (b2Pos){ ox, oy }, t, b2DefaultQueryFilter(), ray_callback, &c );
	qsort( out, (size_t)c.count, sizeof( PB2RayHit ), ray_cmp );
	return c.count;
}

typedef struct OverlapCtx
{
	uint32_t mask;
	int hitSensors;
	int32_t* out;
	int capacity;
	int count;
} OverlapCtx;

static bool overlap_callback( b2ShapeId shape, void* context )
{
	OverlapCtx* c = (OverlapCtx*)context;
	uint32_t u = shape_user( shape );
	if ( !( ( c->mask >> user_layer( u ) ) & 1u ) )
	{
		return true;
	}
	if ( !c->hitSensors && b2Shape_IsSensor( shape ) )
	{
		return true;
	}
	if ( c->count >= c->capacity )
	{
		return false; // buffer full: stop
	}
	c->out[c->count++] = user_collider( u );
	return true;
}

static int overlap_proxy( const b2Vec2* pts, int n, float radius, uint32_t mask, int hitSensors, int32_t* out, int capacity )
{
	OverlapCtx c = { mask, hitSensors, out, capacity, 0 };
	b2ShapeProxy proxy = b2MakeProxy( pts, n, radius );
	b2World_OverlapShape( g_world, (b2Pos){ 0.0f, 0.0f }, &proxy, b2DefaultQueryFilter(), overlap_callback, &c );
	return c.count;
}

int pb2_overlap_point( float x, float y, uint32_t mask, int hitSensors, int32_t* out, int capacity )
{
	b2Vec2 p = { x, y };
	return overlap_proxy( &p, 1, 0.0f, mask, hitSensors, out, capacity );
}

int pb2_overlap_circle( float cx, float cy, float radius, uint32_t mask, int hitSensors, int32_t* out, int capacity )
{
	b2Vec2 p = { cx, cy };
	return overlap_proxy( &p, 1, radius, mask, hitSensors, out, capacity );
}

int pb2_overlap_box( float cx, float cy, float hw, float hh, float angle, uint32_t mask, int hitSensors, int32_t* out,
					 int capacity )
{
	float c = cosf( angle ), s = sinf( angle );
	b2Vec2 corners[4] = { { -hw, -hh }, { hw, -hh }, { hw, hh }, { -hw, hh } };
	for ( int i = 0; i < 4; ++i )
	{
		float x = corners[i].x, y = corners[i].y;
		corners[i] = (b2Vec2){ cx + c * x - s * y, cy + s * x + c * y };
	}
	return overlap_proxy( corners, 4, 0.0f, mask, hitSensors, out, capacity );
}
