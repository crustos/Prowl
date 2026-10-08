# Levels: one input range, gamma and output range for red, green and blue alike.
# Ported from OpenToonz, toonz/sources/stdfx/igs_levels.cpp (BSD-3-Clause, see OPENTOONZ-LICENSE.txt): the input range is stretched to 0..1, bent by
# a gamma (v^(1/gamma)), and put into the output range. With "clamp" off a value above 1 may stay above 1 until it is stored (and then it is clamped).
id: 6
name: levels
title: Levels
group: Color
param: float inMin min=0 max=1 default=0 label="Input low"
param: float inMax min=0 max=1 default=1 label="Input high"
param: float gamma min=0.1 max=10 default=1 label="Gamma"
param: float outMin min=0 max=1 default=0 label="Output low"
param: float outMax min=0 max=1 default=1 label="Output high"
param: bool clampSw default=true label="Clamp"

--- glsl_lib
float fxlv_a_lv(float v, float inMin, float inMax, float gamma, float outMin, float outMax, bool clampSw) {
  v = inMax == inMin ? inMax : (v - inMin) / (inMax - inMin);
  if (clampSw || abs(outMax - 1.0) > 1e-6) v = clamp(v, 0.0, 1.0); else v = max(v, 0.0);
  if (gamma != 1.0 && gamma != 0.0) {
    if (v > 0.0 && v < 1.0) v = pow(v, 1.0 / gamma);
    else if (v > 1.0) v = 1.0 + (v - 1.0) / gamma;
  }
  v = outMin + v * (outMax - outMin);
  return clampSw ? clamp(v, 0.0, 1.0) : max(v, 0.0);
}

--- glsl
c.rgb = vec3(fxlv_a_lv(c.r, inMin, inMax, gamma, outMin, outMax, clampSw), fxlv_a_lv(c.g, inMin, inMax, gamma, outMin, outMax, clampSw), fxlv_a_lv(c.b, inMin, inMax, gamma, outMin, outMax, clampSw));

--- wgsl_lib
fn fxlv_a_lv(v_in: f32, inMin: f32, inMax: f32, gamma: f32, outMin: f32, outMax: f32, clampSw: bool) -> f32 {
  var v = select((v_in - inMin) / (inMax - inMin), inMax, inMax == inMin);
  if (clampSw || abs(outMax - 1.0) > 1e-6) { v = clamp(v, 0.0, 1.0); } else { v = max(v, 0.0); }
  if (gamma != 1.0 && gamma != 0.0) {
    if (v > 0.0 && v < 1.0) { v = pow(v, 1.0 / gamma); }
    else if (v > 1.0) { v = 1.0 + (v - 1.0) / gamma; }
  }
  v = outMin + v * (outMax - outMin);
  return select(max(v, 0.0), clamp(v, 0.0, 1.0), clampSw);
}

--- wgsl
c = vec4f(fxlv_a_lv(c.r, inMin, inMax, gamma, outMin, outMax, clampSw), fxlv_a_lv(c.g, inMin, inMax, gamma, outMin, outMax, clampSw), fxlv_a_lv(c.b, inMin, inMax, gamma, outMin, outMax, clampSw), c.a);

--- c_lib
static float fxlv_a_lv( float v, float inMin, float inMax, float gamma, float outMin, float outMax, int clampSw )
{
	v = inMax == inMin ? inMax : ( v - inMin ) / ( inMax - inMin );
	if ( clampSw || fabsf( outMax - 1.f ) > 1e-6f )
		v = v < 0.f ? 0.f : v > 1.f ? 1.f : v;
	else if ( v < 0.f )
		v = 0.f;
	if ( gamma != 1.f && gamma != 0.f )
	{
		if ( v > 0.f && v < 1.f )
			v = pow_det( v, 1.f / gamma );
		else if ( v > 1.f )
			v = 1.f + ( v - 1.f ) / gamma;
	}
	v = outMin + v * ( outMax - outMin );
	if ( v < 0.f )
		v = 0.f;
	else if ( clampSw && v > 1.f )
		v = 1.f;
	return v;
}

--- c
int i;
for ( i = 0; i < 3; i++ )
	c[i] = fxlv_a_lv( c[i], inMin, inMax, gamma, outMin, outMax, clampSw );
