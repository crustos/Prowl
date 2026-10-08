# Levels per channel: its own input range, gamma and output range for each of red, green and blue.
# Ported from OpenToonz, toonz/sources/stdfx/igs_levels.cpp (BSD-3-Clause, see OPENTOONZ-LICENSE.txt): the input range is stretched to 0..1, bent by
# a gamma (v^(1/gamma)), and put into the output range. With "clamp" off a value above 1 may stay above 1 until it is stored (and then it is clamped).
id: 7
name: levels_rgb
title: Levels (RGB)
group: Color
param: float redInMin min=0 max=1 default=0 label="Red input low"
param: float redInMax min=0 max=1 default=1 label="Red input high"
param: float redGamma min=0.1 max=10 default=1 label="Red gamma"
param: float redOutMin min=0 max=1 default=0 label="Red output low"
param: float redOutMax min=0 max=1 default=1 label="Red output high"
param: float greenInMin min=0 max=1 default=0 label="Green input low"
param: float greenInMax min=0 max=1 default=1 label="Green input high"
param: float greenGamma min=0.1 max=10 default=1 label="Green gamma"
param: float greenOutMin min=0 max=1 default=0 label="Green output low"
param: float greenOutMax min=0 max=1 default=1 label="Green output high"
param: float blueInMin min=0 max=1 default=0 label="Blue input low"
param: float blueInMax min=0 max=1 default=1 label="Blue input high"
param: float blueGamma min=0.1 max=10 default=1 label="Blue gamma"
param: float blueOutMin min=0 max=1 default=0 label="Blue output low"
param: float blueOutMax min=0 max=1 default=1 label="Blue output high"
param: bool clampSw default=true label="Clamp"

--- glsl_lib
float fxlv_b_lv(float v, float inMin, float inMax, float gamma, float outMin, float outMax, bool clampSw) {
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
c.rgb = vec3(fxlv_b_lv(c.r, redInMin, redInMax, redGamma, redOutMin, redOutMax, clampSw), fxlv_b_lv(c.g, greenInMin, greenInMax, greenGamma, greenOutMin, greenOutMax, clampSw), fxlv_b_lv(c.b, blueInMin, blueInMax, blueGamma, blueOutMin, blueOutMax, clampSw));

--- wgsl_lib
fn fxlv_b_lv(v_in: f32, inMin: f32, inMax: f32, gamma: f32, outMin: f32, outMax: f32, clampSw: bool) -> f32 {
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
c = vec4f(fxlv_b_lv(c.r, redInMin, redInMax, redGamma, redOutMin, redOutMax, clampSw), fxlv_b_lv(c.g, greenInMin, greenInMax, greenGamma, greenOutMin, greenOutMax, clampSw), fxlv_b_lv(c.b, blueInMin, blueInMax, blueGamma, blueOutMin, blueOutMax, clampSw), c.a);

--- c_lib
static float fxlv_b_lv( float v, float inMin, float inMax, float gamma, float outMin, float outMax, int clampSw )
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
c[0] = fxlv_b_lv( c[0], redInMin, redInMax, redGamma, redOutMin, redOutMax, clampSw );
c[1] = fxlv_b_lv( c[1], greenInMin, greenInMax, greenGamma, greenOutMin, greenOutMax, clampSw );
c[2] = fxlv_b_lv( c[2], blueInMin, blueInMax, blueGamma, blueOutMin, blueOutMax, clampSw );
