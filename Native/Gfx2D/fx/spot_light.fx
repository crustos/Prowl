# One spot light: a cone from a point, in a direction, over an ambient color.
# Written for Prowl2D (not ported). The picture is lit like a flat 2D scene: every pixel is multiplied by the ambient color plus the light that reaches it, and "glow"
# also adds the light on top, so a lamp shows even over black. Positions are fractions of the picture (Y from the top); a radius is a fraction of the picture's HEIGHT, so
# a light keeps its shape when the window changes size.
id: 15
name: spot_light
title: Spot Light
group: Light
param: color ambient default=0.12,0.13,0.2,1 label="Ambient"
param: float x min=0 max=1 default=0.5 label="X"
param: float y min=0 max=1 default=0.1 label="Y"
param: float angle min=-360 max=360 default=-90 label="Direction"
param: float cone min=1 max=89 default=30 label="Cone half-angle"
param: float softness min=0 max=1 default=0.4 label="Edge softness"
param: float radius min=0.01 max=3 default=1 label="Reach"
param: float intensity min=0 max=4 default=1.2 label="Intensity"
param: enum falloff options=Linear,Smooth,Quadratic default=1 label="Falloff"
param: color color default=1,0.95,0.8,1 label="Color"
param: float glow min=0 max=1 default=0.1 label="Glow"

--- glsl
float ex = px - x * pw;
float ey = y * ph - py;
float dist = length(vec2(ex, ey));
float a = radians(angle);
float cosang = dist > 0.0 ? (ex * cos(a) + ey * sin(a)) / dist : 1.0;
float c0 = cos(radians(cone));
float c1 = cos(radians(cone * (1.0 - softness)));
float f = clamp((cosang - c0) / max(c1 - c0, 0.0001), 0.0, 1.0);
f = f * f * (3.0 - 2.0 * f);
float t = clamp(1.0 - dist / ph / radius, 0.0, 1.0);
if (falloff == 1) t = t * t * (3.0 - 2.0 * t); else if (falloff == 2) t = t * t;
vec3 l = color.rgb * (intensity * t * f);
c.rgb = c.rgb * (ambient.rgb + l) + l * glow;

--- wgsl
let ex = px - x * pw;
let ey = y * ph - py;
let dist = length(vec2f(ex, ey));
let a = radians(angle);
var cosang = 1.0;
if (dist > 0.0) { cosang = (ex * cos(a) + ey * sin(a)) / dist; }
let c0 = cos(radians(cone));
let c1 = cos(radians(cone * (1.0 - softness)));
var f = clamp((cosang - c0) / max(c1 - c0, 0.0001), 0.0, 1.0);
f = f * f * (3.0 - 2.0 * f);
var t = clamp(1.0 - dist / ph / radius, 0.0, 1.0);
if (falloff == 1) { t = t * t * (3.0 - 2.0 * t); } else if (falloff == 2) { t = t * t; }
let l = color.rgb * (intensity * t * f);
c = vec4f(c.rgb * (ambient.rgb + l) + l * glow, c.a);

--- c
float ex = px - x * pw, ey = y * ph - py;
float dist = sqrtf( ex * ex + ey * ey );
float a = angle * 0.017453292519943295f;
float cosang = dist > 0.f ? ( ex * cos_det( a ) + ey * sin_det( a ) ) / dist : 1.f;
float c0 = cos_det( cone * 0.017453292519943295f );
float c1 = cos_det( cone * ( 1.f - softness ) * 0.017453292519943295f );
float den = c1 - c0, f, t;
int i;
den = den > 0.0001f ? den : 0.0001f;
f = ( cosang - c0 ) / den;
f = f < 0.f ? 0.f : f > 1.f ? 1.f : f;
f = f * f * ( 3.f - 2.f * f );
t = 1.f - dist / ph / radius;
t = t < 0.f ? 0.f : t > 1.f ? 1.f : t;
if ( falloff == 1 )
	t = t * t * ( 3.f - 2.f * t );
else if ( falloff == 2 )
	t = t * t;
for ( i = 0; i < 3; i++ )
{
	float l = color[i] * ( intensity * t * f );
	c[i] = c[i] * ( ambient[i] + l ) + l * glow;
}
