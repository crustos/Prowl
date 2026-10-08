# The lights of a level: up to eight lights, each a point light or a spot light, over an ambient color. The Lights window of the editor drives this one (it places the lights
# in the level and turns them into the picture's fractions each frame); the generic effect list does not offer it. Written for Prowl2D (not ported).
# A pixel becomes picture * (ambient + exposure * light) + exposure * light * glow, mixed with the unlit picture by "mix". A light with intensity 0 is off. A
# position is a fraction of the picture (Y from the top), a radius a fraction of its height, an angle is in degrees (0: right, 90: up).
id: 16
name: scene_lights
title: Scene Lights
group: Light
hidden: true
param: color ambient default=0.18,0.2,0.3,1 label="Ambient"
param: enum falloff options=Linear,Smooth,Quadratic default=1 label="Falloff"
param: float glow min=0 max=1 default=0.15 label="Glow"
param: float exposure min=0 max=4 default=1 label="Exposure"
param: float mixAmt min=0 max=1 default=1 label="Mix"
param: float l0x min=0 max=1 default=0.5 label="Light 1 X"
param: float l0y min=0 max=1 default=0.5 label="Light 1 Y"
param: float l0r min=0 max=3 default=0.3 label="Light 1 radius"
param: float l0k min=0 max=4 default=0 label="Light 1 intensity"
param: color l0c default=1,0.9,0.7,1 label="Light 1 color"
param: float l0a min=-360 max=360 default=-90 label="Light 1 direction"
param: float l0o min=1 max=89 default=30 label="Light 1 cone"
param: float l0s min=0 max=1 default=0.4 label="Light 1 softness"
param: enum l0t options=Point,Spot default=0 label="Light 1 kind"
param: float l1x min=0 max=1 default=0.5 label="Light 2 X"
param: float l1y min=0 max=1 default=0.5 label="Light 2 Y"
param: float l1r min=0 max=3 default=0.3 label="Light 2 radius"
param: float l1k min=0 max=4 default=0 label="Light 2 intensity"
param: color l1c default=1,0.9,0.7,1 label="Light 2 color"
param: float l1a min=-360 max=360 default=-90 label="Light 2 direction"
param: float l1o min=1 max=89 default=30 label="Light 2 cone"
param: float l1s min=0 max=1 default=0.4 label="Light 2 softness"
param: enum l1t options=Point,Spot default=0 label="Light 2 kind"
param: float l2x min=0 max=1 default=0.5 label="Light 3 X"
param: float l2y min=0 max=1 default=0.5 label="Light 3 Y"
param: float l2r min=0 max=3 default=0.3 label="Light 3 radius"
param: float l2k min=0 max=4 default=0 label="Light 3 intensity"
param: color l2c default=1,0.9,0.7,1 label="Light 3 color"
param: float l2a min=-360 max=360 default=-90 label="Light 3 direction"
param: float l2o min=1 max=89 default=30 label="Light 3 cone"
param: float l2s min=0 max=1 default=0.4 label="Light 3 softness"
param: enum l2t options=Point,Spot default=0 label="Light 3 kind"
param: float l3x min=0 max=1 default=0.5 label="Light 4 X"
param: float l3y min=0 max=1 default=0.5 label="Light 4 Y"
param: float l3r min=0 max=3 default=0.3 label="Light 4 radius"
param: float l3k min=0 max=4 default=0 label="Light 4 intensity"
param: color l3c default=1,0.9,0.7,1 label="Light 4 color"
param: float l3a min=-360 max=360 default=-90 label="Light 4 direction"
param: float l3o min=1 max=89 default=30 label="Light 4 cone"
param: float l3s min=0 max=1 default=0.4 label="Light 4 softness"
param: enum l3t options=Point,Spot default=0 label="Light 4 kind"
param: float l4x min=0 max=1 default=0.5 label="Light 5 X"
param: float l4y min=0 max=1 default=0.5 label="Light 5 Y"
param: float l4r min=0 max=3 default=0.3 label="Light 5 radius"
param: float l4k min=0 max=4 default=0 label="Light 5 intensity"
param: color l4c default=1,0.9,0.7,1 label="Light 5 color"
param: float l4a min=-360 max=360 default=-90 label="Light 5 direction"
param: float l4o min=1 max=89 default=30 label="Light 5 cone"
param: float l4s min=0 max=1 default=0.4 label="Light 5 softness"
param: enum l4t options=Point,Spot default=0 label="Light 5 kind"
param: float l5x min=0 max=1 default=0.5 label="Light 6 X"
param: float l5y min=0 max=1 default=0.5 label="Light 6 Y"
param: float l5r min=0 max=3 default=0.3 label="Light 6 radius"
param: float l5k min=0 max=4 default=0 label="Light 6 intensity"
param: color l5c default=1,0.9,0.7,1 label="Light 6 color"
param: float l5a min=-360 max=360 default=-90 label="Light 6 direction"
param: float l5o min=1 max=89 default=30 label="Light 6 cone"
param: float l5s min=0 max=1 default=0.4 label="Light 6 softness"
param: enum l5t options=Point,Spot default=0 label="Light 6 kind"
param: float l6x min=0 max=1 default=0.5 label="Light 7 X"
param: float l6y min=0 max=1 default=0.5 label="Light 7 Y"
param: float l6r min=0 max=3 default=0.3 label="Light 7 radius"
param: float l6k min=0 max=4 default=0 label="Light 7 intensity"
param: color l6c default=1,0.9,0.7,1 label="Light 7 color"
param: float l6a min=-360 max=360 default=-90 label="Light 7 direction"
param: float l6o min=1 max=89 default=30 label="Light 7 cone"
param: float l6s min=0 max=1 default=0.4 label="Light 7 softness"
param: enum l6t options=Point,Spot default=0 label="Light 7 kind"
param: float l7x min=0 max=1 default=0.5 label="Light 8 X"
param: float l7y min=0 max=1 default=0.5 label="Light 8 Y"
param: float l7r min=0 max=3 default=0.3 label="Light 8 radius"
param: float l7k min=0 max=4 default=0 label="Light 8 intensity"
param: color l7c default=1,0.9,0.7,1 label="Light 8 color"
param: float l7a min=-360 max=360 default=-90 label="Light 8 direction"
param: float l7o min=1 max=89 default=30 label="Light 8 cone"
param: float l7s min=0 max=1 default=0.4 label="Light 8 softness"
param: enum l7t options=Point,Spot default=0 label="Light 8 kind"

--- glsl_lib
vec3 fxsl_one(float px, float py, float pw, float ph, float x, float y, float r, float k, vec4 col, float ang, float cone, float soft, int kind, int curve) {
  if (k <= 0.0 || r <= 0.0) return vec3(0.0);
  float ex = px - x * pw, ey = y * ph - py;
  float dist = length(vec2(ex, ey));
  float t = clamp(1.0 - dist / ph / r, 0.0, 1.0);
  if (curve == 1) t = t * t * (3.0 - 2.0 * t); else if (curve == 2) t = t * t;
  if (kind == 1) {
    float a = radians(ang);
    float cosang = dist > 0.0 ? (ex * cos(a) + ey * sin(a)) / dist : 1.0;
    float c0 = cos(radians(cone)), c1 = cos(radians(cone * (1.0 - soft)));
    float f = clamp((cosang - c0) / max(c1 - c0, 0.0001), 0.0, 1.0);
    t *= f * f * (3.0 - 2.0 * f);
  }
  return col.rgb * (k * t);
}

--- glsl
vec3 l = fxsl_one(px, py, pw, ph, l0x, l0y, l0r, l0k, l0c, l0a, l0o, l0s, l0t, falloff) + fxsl_one(px, py, pw, ph, l1x, l1y, l1r, l1k, l1c, l1a, l1o, l1s, l1t, falloff) + fxsl_one(px, py, pw, ph, l2x, l2y, l2r, l2k, l2c, l2a, l2o, l2s, l2t, falloff) + fxsl_one(px, py, pw, ph, l3x, l3y, l3r, l3k, l3c, l3a, l3o, l3s, l3t, falloff) + fxsl_one(px, py, pw, ph, l4x, l4y, l4r, l4k, l4c, l4a, l4o, l4s, l4t, falloff) + fxsl_one(px, py, pw, ph, l5x, l5y, l5r, l5k, l5c, l5a, l5o, l5s, l5t, falloff) + fxsl_one(px, py, pw, ph, l6x, l6y, l6r, l6k, l6c, l6a, l6o, l6s, l6t, falloff) + fxsl_one(px, py, pw, ph, l7x, l7y, l7r, l7k, l7c, l7a, l7o, l7s, l7t, falloff);
l *= exposure;
c.rgb = mix(c.rgb, c.rgb * (ambient.rgb + l) + l * glow, mixAmt);

--- wgsl_lib
fn fxsl_one(px: f32, py: f32, pw: f32, ph: f32, x: f32, y: f32, r: f32, k: f32, col: vec4f, ang: f32, cone: f32, soft: f32, kind: i32, curve: i32) -> vec3f {
  if (k <= 0.0 || r <= 0.0) { return vec3f(0.0); }
  let ex = px - x * pw;
  let ey = y * ph - py;
  let dist = length(vec2f(ex, ey));
  var t = clamp(1.0 - dist / ph / r, 0.0, 1.0);
  if (curve == 1) { t = t * t * (3.0 - 2.0 * t); } else if (curve == 2) { t = t * t; }
  if (kind == 1) {
    let a = radians(ang);
    var cosang = 1.0;
    if (dist > 0.0) { cosang = (ex * cos(a) + ey * sin(a)) / dist; }
    let c0 = cos(radians(cone));
    let c1 = cos(radians(cone * (1.0 - soft)));
    let f = clamp((cosang - c0) / max(c1 - c0, 0.0001), 0.0, 1.0);
    t = t * (f * f * (3.0 - 2.0 * f));
  }
  return col.rgb * (k * t);
}

--- wgsl
var l = fxsl_one(px, py, pw, ph, l0x, l0y, l0r, l0k, l0c, l0a, l0o, l0s, l0t, falloff) + fxsl_one(px, py, pw, ph, l1x, l1y, l1r, l1k, l1c, l1a, l1o, l1s, l1t, falloff) + fxsl_one(px, py, pw, ph, l2x, l2y, l2r, l2k, l2c, l2a, l2o, l2s, l2t, falloff) + fxsl_one(px, py, pw, ph, l3x, l3y, l3r, l3k, l3c, l3a, l3o, l3s, l3t, falloff) + fxsl_one(px, py, pw, ph, l4x, l4y, l4r, l4k, l4c, l4a, l4o, l4s, l4t, falloff) + fxsl_one(px, py, pw, ph, l5x, l5y, l5r, l5k, l5c, l5a, l5o, l5s, l5t, falloff) + fxsl_one(px, py, pw, ph, l6x, l6y, l6r, l6k, l6c, l6a, l6o, l6s, l6t, falloff) + fxsl_one(px, py, pw, ph, l7x, l7y, l7r, l7k, l7c, l7a, l7o, l7s, l7t, falloff);
l = l * exposure;
c = vec4f(mix(c.rgb, c.rgb * (ambient.rgb + l) + l * glow, vec3f(mixAmt)), c.a);

--- c_lib
static void fxsl_one( float* out, float px, float py, float pw, float ph, float x, float y, float r, float k, const float* col, float ang, float cone, float soft, int kind, int curve )
{
	float ex = px - x * pw, ey = y * ph - py, dist, t;
	int i;
	for ( i = 0; i < 3; i++ )
		out[i] = 0.f;
	if ( k <= 0.f || r <= 0.f )
		return;
	dist = sqrtf( ex * ex + ey * ey );
	t = 1.f - dist / ph / r;
	t = t < 0.f ? 0.f : t > 1.f ? 1.f : t;
	if ( curve == 1 )
		t = t * t * ( 3.f - 2.f * t );
	else if ( curve == 2 )
		t = t * t;
	if ( kind == 1 )
	{
		float a = ang * 0.017453292519943295f, cosang = dist > 0.f ? ( ex * cos_det( a ) + ey * sin_det( a ) ) / dist : 1.f;
		float c0 = cos_det( cone * 0.017453292519943295f ), c1 = cos_det( cone * ( 1.f - soft ) * 0.017453292519943295f ), den = c1 - c0, f;
		den = den > 0.0001f ? den : 0.0001f;
		f = ( cosang - c0 ) / den;
		f = f < 0.f ? 0.f : f > 1.f ? 1.f : f;
		t *= f * f * ( 3.f - 2.f * f );
	}
	for ( i = 0; i < 3; i++ )
		out[i] = col[i] * ( k * t );
}

--- c
float l[3] = { 0.f, 0.f, 0.f }, t[3], lit;
int i;
fxsl_one( t, px, py, pw, ph, l0x, l0y, l0r, l0k, l0c, l0a, l0o, l0s, l0t, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
fxsl_one( t, px, py, pw, ph, l1x, l1y, l1r, l1k, l1c, l1a, l1o, l1s, l1t, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
fxsl_one( t, px, py, pw, ph, l2x, l2y, l2r, l2k, l2c, l2a, l2o, l2s, l2t, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
fxsl_one( t, px, py, pw, ph, l3x, l3y, l3r, l3k, l3c, l3a, l3o, l3s, l3t, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
fxsl_one( t, px, py, pw, ph, l4x, l4y, l4r, l4k, l4c, l4a, l4o, l4s, l4t, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
fxsl_one( t, px, py, pw, ph, l5x, l5y, l5r, l5k, l5c, l5a, l5o, l5s, l5t, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
fxsl_one( t, px, py, pw, ph, l6x, l6y, l6r, l6k, l6c, l6a, l6o, l6s, l6t, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
fxsl_one( t, px, py, pw, ph, l7x, l7y, l7r, l7k, l7c, l7a, l7o, l7s, l7t, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
for ( i = 0; i < 3; i++ )
{
	l[i] *= exposure;
	lit = c[i] * ( ambient[i] + l[i] ) + l[i] * glow;
	c[i] = c[i] + ( lit - c[i] ) * mixAmt;
}
