# HSV Adjust: each of hue, value and saturation can be moved around a pivot, scaled and shifted (OpenToonz "HSV Adjust").
# Ported from OpenToonz, toonz/sources/stdfx/igs_hsv_adjust.cpp and igs_color_rgb_hsv.cpp (BSD-3-Clause, see OPENTOONZ-LICENSE.txt).
# Hue is in degrees; it is turned around its pivot the short way (-180..180) before it is scaled. (OpenToonz's handling of negative colors is left out: a picture's colors are 0..1.)
id: 4
name: hsv_adjust
title: HSV Adjust
group: Color
param: float huePivot min=0 max=360 default=0 label="Hue pivot"
param: float hueScale min=-4 max=4 default=1 label="Hue scale"
param: float hueShift min=-360 max=360 default=0 label="Hue shift"
param: float valPivot min=0 max=1 default=0 label="Value pivot"
param: float valScale min=0 max=4 default=1 label="Value scale"
param: float valShift min=-1 max=1 default=0 label="Value shift"
param: float satPivot min=0 max=1 default=0 label="Saturation pivot"
param: float satScale min=0 max=4 default=1 label="Saturation scale"
param: float satShift min=-1 max=1 default=0 label="Saturation shift"


--- glsl_lib
vec3 fxh_to_hsv(vec3 c) {
  float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
  float h = 0.0, s = 0.0, d = mx - mn;
  if (mx != mn) {
    s = d / mx;
    if (c.r == mx) h = (c.g - c.b) / d;
    else if (c.g == mx) h = 2.0 + (c.b - c.r) / d;
    else h = 4.0 + (c.r - c.g) / d;
    h *= 60.0;
    if (h < 0.0) h += 360.0;
  }
  return vec3(h, s, mx);
}
vec3 fxh_from_hsv(float h, float s, float v) {
  if (s == 0.0) return vec3(v);
  float hh = (h - 360.0 * floor(h / 360.0)) / 60.0;
  if (hh >= 6.0) hh -= 6.0;
  float i = floor(hh), f = hh - i;
  float p = v * (1.0 - s), q = v * (1.0 - s * f), t = v * (1.0 - s * (1.0 - f));
  int k = int(i);
  if (k == 0) return vec3(v, t, p);
  if (k == 1) return vec3(q, v, p);
  if (k == 2) return vec3(p, v, t);
  if (k == 3) return vec3(p, q, v);
  if (k == 4) return vec3(t, p, v);
  return vec3(v, p, q);
}

--- glsl
vec3 hsv = fxh_to_hsv(c.rgb);
if (hueScale != 1.0 || hueShift != 0.0) {
  float d = hsv.x - huePivot;
  d = d - 360.0 * floor((d + 180.0) / 360.0);
  float h = d * hueScale + huePivot + hueShift;
  hsv.x = h - 360.0 * floor(h / 360.0);
}
if (satScale != 1.0 || satShift != 0.0) hsv.y = max(0.0, (hsv.y - satPivot) * satScale + satPivot + satShift);
if (valScale != 1.0 || valShift != 0.0) hsv.z = (hsv.z - valPivot) * valScale + valPivot + valShift;
c.rgb = fxh_from_hsv(hsv.x, hsv.y, hsv.z);

--- wgsl_lib
fn fxh_to_hsv(c: vec3f) -> vec3f {
  let mx = max(c.r, max(c.g, c.b));
  let mn = min(c.r, min(c.g, c.b));
  let d = mx - mn;
  var h = 0.0;
  var s = 0.0;
  if (mx != mn) {
    s = d / mx;
    if (c.r == mx) { h = (c.g - c.b) / d; }
    else if (c.g == mx) { h = 2.0 + (c.b - c.r) / d; }
    else { h = 4.0 + (c.r - c.g) / d; }
    h = h * 60.0;
    if (h < 0.0) { h = h + 360.0; }
  }
  return vec3f(h, s, mx);
}
fn fxh_from_hsv(h: f32, s: f32, v: f32) -> vec3f {
  if (s == 0.0) { return vec3f(v); }
  var hh = (h - 360.0 * floor(h / 360.0)) / 60.0;
  if (hh >= 6.0) { hh = hh - 6.0; }
  let i = floor(hh);
  let f = hh - i;
  let p = v * (1.0 - s);
  let q = v * (1.0 - s * f);
  let t = v * (1.0 - s * (1.0 - f));
  let k = i32(i);
  if (k == 0) { return vec3f(v, t, p); }
  if (k == 1) { return vec3f(q, v, p); }
  if (k == 2) { return vec3f(p, v, t); }
  if (k == 3) { return vec3f(p, q, v); }
  if (k == 4) { return vec3f(t, p, v); }
  return vec3f(v, p, q);
}

--- wgsl
var hsv = fxh_to_hsv(c.rgb);
if (hueScale != 1.0 || hueShift != 0.0) {
  var d = hsv.x - huePivot;
  d = d - 360.0 * floor((d + 180.0) / 360.0);
  let h = d * hueScale + huePivot + hueShift;
  hsv.x = h - 360.0 * floor(h / 360.0);
}
if (satScale != 1.0 || satShift != 0.0) { hsv.y = max(0.0, (hsv.y - satPivot) * satScale + satPivot + satShift); }
if (valScale != 1.0 || valShift != 0.0) { hsv.z = (hsv.z - valPivot) * valScale + valPivot + valShift; }
c = vec4f(fxh_from_hsv(hsv.x, hsv.y, hsv.z), c.a);

--- c_lib
static void fxh_to_hsv( const float* c, float* o )
{
	float mx = fmaxf( c[0], fmaxf( c[1], c[2] ) ), mn = fminf( c[0], fminf( c[1], c[2] ) );
	float h = 0.f, s = 0.f, d = mx - mn;
	if ( mx != mn )
	{
		s = d / mx;
		if ( c[0] == mx )
			h = ( c[1] - c[2] ) / d;
		else if ( c[1] == mx )
			h = 2.f + ( c[2] - c[0] ) / d;
		else
			h = 4.f + ( c[0] - c[1] ) / d;
		h *= 60.f;
		if ( h < 0.f )
			h += 360.f;
	}
	o[0] = h; o[1] = s; o[2] = mx;
}
static void fxh_from_hsv( float h, float s, float v, float* o )
{
	float hh, i, f, p, q, t;
	int k;
	if ( s == 0.f )
	{
		o[0] = o[1] = o[2] = v;
		return;
	}
	hh = ( h - 360.f * floorf( h / 360.f ) ) / 60.f;
	if ( hh >= 6.f )
		hh -= 6.f;
	i = floorf( hh ); f = hh - i;
	p = v * ( 1.f - s ); q = v * ( 1.f - s * f ); t = v * ( 1.f - s * ( 1.f - f ) );
	k = (int)i;
	switch ( k )
	{
	case 0: o[0] = v; o[1] = t; o[2] = p; break;
	case 1: o[0] = q; o[1] = v; o[2] = p; break;
	case 2: o[0] = p; o[1] = v; o[2] = t; break;
	case 3: o[0] = p; o[1] = q; o[2] = v; break;
	case 4: o[0] = t; o[1] = p; o[2] = v; break;
	default: o[0] = v; o[1] = p; o[2] = q; break;
	}
}

--- c
float hsv[3], rgb[3];
fxh_to_hsv( c, hsv );
if ( hueScale != 1.f || hueShift != 0.f )
{
	float d = hsv[0] - huePivot, h;
	d = d - 360.f * floorf( ( d + 180.f ) / 360.f );
	h = d * hueScale + huePivot + hueShift;
	hsv[0] = h - 360.f * floorf( h / 360.f );
}
if ( satScale != 1.f || satShift != 0.f )
{
	hsv[1] = ( hsv[1] - satPivot ) * satScale + satPivot + satShift;
	if ( hsv[1] < 0.f )
		hsv[1] = 0.f;
}
if ( valScale != 1.f || valShift != 0.f )
	hsv[2] = ( hsv[2] - valPivot ) * valScale + valPivot + valShift;
fxh_from_hsv( hsv[0], hsv[1], hsv[2], rgb );
c[0] = rgb[0]; c[1] = rgb[1]; c[2] = rgb[2];
