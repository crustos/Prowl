# HLS Adjust: each of hue, lightness and saturation can be moved around a pivot, scaled and shifted (OpenToonz "HLS Adjust").
# Ported from OpenToonz, toonz/sources/stdfx/igs_hls_adjust.cpp and igs_color_rgb_hls.cpp (BSD-3-Clause, see OPENTOONZ-LICENSE.txt).
# Hue is in degrees; it is turned around its pivot the short way (-180..180) before it is scaled. With 'cylindrical' off, saturation is the spread of the colors (conical HLS), as OpenToonz offers.
id: 5
name: hls_adjust
title: HLS Adjust
group: Color
param: float huePivot min=0 max=360 default=0 label="Hue pivot"
param: float hueScale min=-4 max=4 default=1 label="Hue scale"
param: float hueShift min=-360 max=360 default=0 label="Hue shift"
param: float ligPivot min=0 max=1 default=0 label="Lightness pivot"
param: float ligScale min=0 max=4 default=1 label="Lightness scale"
param: float ligShift min=-1 max=1 default=0 label="Lightness shift"
param: float satPivot min=0 max=1 default=0 label="Saturation pivot"
param: float satScale min=0 max=4 default=1 label="Saturation scale"
param: float satShift min=-1 max=1 default=0 label="Saturation shift"
param: bool cylindrical default=true label="Cylindrical"


--- glsl_lib
vec3 fxl_to_hls(vec3 c, bool cyl) {
  float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
  float h = 0.0, s = 0.0, l = (mx + mn) / 2.0;
  if (mx != mn) {
    float d = mx - mn;
    if (cyl) s = l <= 0.5 ? d / (mx + mn) : d / (2.0 - (mx + mn));
    else s = d;
    float rm = (mx - c.r) / d, gm = (mx - c.g) / d, bm = (mx - c.b) / d;
    if (c.r == mx) h = bm - gm;
    else if (c.g == mx) h = 2.0 + rm - bm;
    else h = 4.0 + gm - rm;
    h *= 60.0;
    if (h < 0.0) h += 360.0;
  }
  return vec3(h, l, s);
}
float fxl_calc(float m1, float m2, float hue) {
  float hh = hue - 360.0 * floor(hue / 360.0);
  if (hh < 60.0) return m1 + (m2 - m1) * hh / 60.0;
  if (hh < 180.0) return m2;
  if (hh < 240.0) return m1 + (m2 - m1) * (240.0 - hh) / 60.0;
  return m1;
}
vec3 fxl_from_hls(float h, float l, float s, bool cyl) {
  if (s == 0.0) return vec3(l);
  float m2, m1;
  if (cyl) {
    m2 = l <= 0.5 ? l * (1.0 + s) : l + s - l * s;
    m1 = 2.0 * l - m2;
  } else {
    m2 = l + s * 0.5;
    m1 = l - s * 0.5;
  }
  return vec3(fxl_calc(m1, m2, h + 120.0), fxl_calc(m1, m2, h), fxl_calc(m1, m2, h - 120.0));
}

--- glsl
vec3 hls = fxl_to_hls(c.rgb, cylindrical);
if (hueScale != 1.0 || hueShift != 0.0) {
  float d = hls.x - huePivot;
  d = d - 360.0 * floor((d + 180.0) / 360.0);
  float h = d * hueScale + huePivot + hueShift;
  hls.x = h - 360.0 * floor(h / 360.0);
}
if (ligScale != 1.0 || ligShift != 0.0) hls.y = (hls.y - ligPivot) * ligScale + ligPivot + ligShift;
if (satScale != 1.0 || satShift != 0.0) hls.z = (hls.z - satPivot) * satScale + satPivot + satShift;
c.rgb = fxl_from_hls(hls.x, hls.y, hls.z, cylindrical);

--- wgsl_lib
fn fxl_to_hls(c: vec3f, cyl: bool) -> vec3f {
  let mx = max(c.r, max(c.g, c.b));
  let mn = min(c.r, min(c.g, c.b));
  let l = (mx + mn) / 2.0;
  var h = 0.0;
  var s = 0.0;
  if (mx != mn) {
    let d = mx - mn;
    if (cyl) { s = select(d / (2.0 - (mx + mn)), d / (mx + mn), l <= 0.5); } else { s = d; }
    let rm = (mx - c.r) / d;
    let gm = (mx - c.g) / d;
    let bm = (mx - c.b) / d;
    if (c.r == mx) { h = bm - gm; }
    else if (c.g == mx) { h = 2.0 + rm - bm; }
    else { h = 4.0 + gm - rm; }
    h = h * 60.0;
    if (h < 0.0) { h = h + 360.0; }
  }
  return vec3f(h, l, s);
}
fn fxl_calc(m1: f32, m2: f32, hue: f32) -> f32 {
  let hh = hue - 360.0 * floor(hue / 360.0);
  if (hh < 60.0) { return m1 + (m2 - m1) * hh / 60.0; }
  if (hh < 180.0) { return m2; }
  if (hh < 240.0) { return m1 + (m2 - m1) * (240.0 - hh) / 60.0; }
  return m1;
}
fn fxl_from_hls(h: f32, l: f32, s: f32, cyl: bool) -> vec3f {
  if (s == 0.0) { return vec3f(l); }
  var m2 = l + s * 0.5;
  var m1 = l - s * 0.5;
  if (cyl) {
    m2 = select(l + s - l * s, l * (1.0 + s), l <= 0.5);
    m1 = 2.0 * l - m2;
  }
  return vec3f(fxl_calc(m1, m2, h + 120.0), fxl_calc(m1, m2, h), fxl_calc(m1, m2, h - 120.0));
}

--- wgsl
var hls = fxl_to_hls(c.rgb, cylindrical);
if (hueScale != 1.0 || hueShift != 0.0) {
  var d = hls.x - huePivot;
  d = d - 360.0 * floor((d + 180.0) / 360.0);
  let h = d * hueScale + huePivot + hueShift;
  hls.x = h - 360.0 * floor(h / 360.0);
}
if (ligScale != 1.0 || ligShift != 0.0) { hls.y = (hls.y - ligPivot) * ligScale + ligPivot + ligShift; }
if (satScale != 1.0 || satShift != 0.0) { hls.z = (hls.z - satPivot) * satScale + satPivot + satShift; }
c = vec4f(fxl_from_hls(hls.x, hls.y, hls.z, cylindrical), c.a);

--- c_lib
static void fxl_to_hls( const float* c, int cyl, float* o )
{
	float mx = fmaxf( c[0], fmaxf( c[1], c[2] ) ), mn = fminf( c[0], fminf( c[1], c[2] ) );
	float h = 0.f, s = 0.f, l = ( mx + mn ) / 2.f;
	if ( mx != mn )
	{
		float d = mx - mn, rm, gm, bm;
		if ( cyl )
			s = l <= 0.5f ? d / ( mx + mn ) : d / ( 2.f - ( mx + mn ) );
		else
			s = d;
		rm = ( mx - c[0] ) / d; gm = ( mx - c[1] ) / d; bm = ( mx - c[2] ) / d;
		if ( c[0] == mx )
			h = bm - gm;
		else if ( c[1] == mx )
			h = 2.f + rm - bm;
		else
			h = 4.f + gm - rm;
		h *= 60.f;
		if ( h < 0.f )
			h += 360.f;
	}
	o[0] = h; o[1] = l; o[2] = s;
}
static float fxl_calc( float m1, float m2, float hue )
{
	float hh = hue - 360.f * floorf( hue / 360.f );
	if ( hh < 60.f )
		return m1 + ( m2 - m1 ) * hh / 60.f;
	if ( hh < 180.f )
		return m2;
	if ( hh < 240.f )
		return m1 + ( m2 - m1 ) * ( 240.f - hh ) / 60.f;
	return m1;
}
static void fxl_from_hls( float h, float l, float s, int cyl, float* o )
{
	float m1, m2;
	if ( s == 0.f )
	{
		o[0] = o[1] = o[2] = l;
		return;
	}
	if ( cyl )
	{
		m2 = l <= 0.5f ? l * ( 1.f + s ) : l + s - l * s;
		m1 = 2.f * l - m2;
	}
	else
	{
		m2 = l + s * 0.5f;
		m1 = l - s * 0.5f;
	}
	o[0] = fxl_calc( m1, m2, h + 120.f ); o[1] = fxl_calc( m1, m2, h ); o[2] = fxl_calc( m1, m2, h - 120.f );
}

--- c
float hls[3], rgb[3];
fxl_to_hls( c, cylindrical, hls );
if ( hueScale != 1.f || hueShift != 0.f )
{
	float d = hls[0] - huePivot, h;
	d = d - 360.f * floorf( ( d + 180.f ) / 360.f );
	h = d * hueScale + huePivot + hueShift;
	hls[0] = h - 360.f * floorf( h / 360.f );
}
if ( ligScale != 1.f || ligShift != 0.f )
	hls[1] = ( hls[1] - ligPivot ) * ligScale + ligPivot + ligShift;
if ( satScale != 1.f || satShift != 0.f )
	hls[2] = ( hls[2] - satPivot ) * satScale + satPivot + satShift;
fxl_from_hls( hls[0], hls[1], hls[2], cylindrical, rgb );
c[0] = rgb[0]; c[1] = rgb[1]; c[2] = rgb[2];
