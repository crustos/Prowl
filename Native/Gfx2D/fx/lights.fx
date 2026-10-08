# Up to three point lights over an ambient color. A light whose intensity is 0 is off.
# Written for Prowl2D (not ported). The picture is lit like a flat 2D scene: every pixel is multiplied by the ambient color plus the light that reaches it, and "glow"
# also adds the light on top, so a lamp shows even over black. Positions are fractions of the picture (Y from the top); a radius is a fraction of the picture's HEIGHT, so
# a light keeps its shape when the window changes size.
id: 14
name: lights
title: Point Lights
group: Light
param: color ambient default=0.18,0.2,0.3,1 label="Ambient"
param: enum falloff options=Linear,Smooth,Quadratic default=1 label="Falloff"
param: float glow min=0 max=1 default=0.15 label="Glow"
param: float l1x min=0 max=1 default=0.3 label="Light 1 X"
param: float l1y min=0 max=1 default=0.5 label="Light 1 Y"
param: float l1r min=0 max=2 default=0.35 label="Light 1 radius"
param: float l1k min=0 max=4 default=1.0 label="Light 1 intensity"
param: color l1c default=1,0.8,0.5,1 label="Light 1 color"
param: float l2x min=0 max=1 default=0.7 label="Light 2 X"
param: float l2y min=0 max=1 default=0.4 label="Light 2 Y"
param: float l2r min=0 max=2 default=0.3 label="Light 2 radius"
param: float l2k min=0 max=4 default=0 label="Light 2 intensity"
param: color l2c default=0.4,0.7,1,1 label="Light 2 color"
param: float l3x min=0 max=1 default=0.5 label="Light 3 X"
param: float l3y min=0 max=1 default=0.8 label="Light 3 Y"
param: float l3r min=0 max=2 default=0.25 label="Light 3 radius"
param: float l3k min=0 max=4 default=0 label="Light 3 intensity"
param: color l3c default=1,0.4,0.6,1 label="Light 3 color"

--- glsl_lib
vec3 fxlt_one(float px, float py, float pw, float ph, float x, float y, float r, float k, vec4 col, int curve) {
  if (k <= 0.0 || r <= 0.0) return vec3(0.0);
  float d = length(vec2(px - x * pw, py - y * ph)) / ph;
  float t = clamp(1.0 - d / r, 0.0, 1.0);
  if (curve == 1) t = t * t * (3.0 - 2.0 * t); else if (curve == 2) t = t * t;
  return col.rgb * (k * t);
}

--- glsl
vec3 l = fxlt_one(px, py, pw, ph, l1x, l1y, l1r, l1k, l1c, falloff) + fxlt_one(px, py, pw, ph, l2x, l2y, l2r, l2k, l2c, falloff) + fxlt_one(px, py, pw, ph, l3x, l3y, l3r, l3k, l3c, falloff);
c.rgb = c.rgb * (ambient.rgb + l) + l * glow;

--- wgsl_lib
fn fxlt_one(px: f32, py: f32, pw: f32, ph: f32, x: f32, y: f32, r: f32, k: f32, col: vec4f, curve: i32) -> vec3f {
  if (k <= 0.0 || r <= 0.0) { return vec3f(0.0); }
  let d = length(vec2f(px - x * pw, py - y * ph)) / ph;
  var t = clamp(1.0 - d / r, 0.0, 1.0);
  if (curve == 1) { t = t * t * (3.0 - 2.0 * t); } else if (curve == 2) { t = t * t; }
  return col.rgb * (k * t);
}

--- wgsl
let l = fxlt_one(px, py, pw, ph, l1x, l1y, l1r, l1k, l1c, falloff) + fxlt_one(px, py, pw, ph, l2x, l2y, l2r, l2k, l2c, falloff) + fxlt_one(px, py, pw, ph, l3x, l3y, l3r, l3k, l3c, falloff);
c = vec4f(c.rgb * (ambient.rgb + l) + l * glow, c.a);

--- c_lib
static void fxlt_one( float* out, float px, float py, float pw, float ph, float x, float y, float r, float k, const float* col, int curve )
{
	float d, t;
	int i;
	for ( i = 0; i < 3; i++ )
		out[i] = 0.f;
	if ( k <= 0.f || r <= 0.f )
		return;
	d = sqrtf( ( px - x * pw ) * ( px - x * pw ) + ( py - y * ph ) * ( py - y * ph ) ) / ph;
	t = 1.f - d / r;
	t = t < 0.f ? 0.f : t > 1.f ? 1.f : t;
	if ( curve == 1 )
		t = t * t * ( 3.f - 2.f * t );
	else if ( curve == 2 )
		t = t * t;
	for ( i = 0; i < 3; i++ )
		out[i] = col[i] * ( k * t );
}

--- c
float l[3], t[3];
int i;
fxlt_one( l, px, py, pw, ph, l1x, l1y, l1r, l1k, l1c, falloff );
fxlt_one( t, px, py, pw, ph, l2x, l2y, l2r, l2k, l2c, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
fxlt_one( t, px, py, pw, ph, l3x, l3y, l3r, l3k, l3c, falloff );
for ( i = 0; i < 3; i++ )
	l[i] += t[i];
for ( i = 0; i < 3; i++ )
	c[i] = c[i] * ( ambient[i] + l[i] ) + l[i] * glow;
