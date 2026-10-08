# Blend a color over the picture with a blend mode. The picture is the "down" layer, the color (with its alpha and the opacity) the "up" layer.
# Ported from OpenToonz, toonz/sources/stdfx/igs_color_blend.cpp (BSD-3-Clause, see OPENTOONZ-LICENSE.txt). Where the picture is opaque OpenToonz's
# blend_transp_ reduces to  result = mix(down, mode(down, up), up_alpha * opacity), which is what is done here, per channel; the two "color" modes
# compare luminance (0.298912 R + 0.586611 G + 0.114478 B), as OpenToonz does. A divide by zero gives what OpenToonz gives (1 for divide, 0 for burn).
id: 3
name: blend
title: Blend Color
group: Blend
param: enum mode options=Normal,Multiply,Divide,ColorBurn,LinearBurn,Darken,DarkerColor,Lighten,Screen,ColorDodge,LinearDodge,LighterColor,Overlay,SoftLight,HardLight,VividLight,LinearLight,PinLight,HardMix default=1 label="Mode"
param: color color default=1,0.5,0.1,1 label="Color"
param: float opacity min=0 max=1 default=1 label="Opacity"

--- glsl_lib
float fxb_burn(float d, float u) { return u <= 0.0 ? 0.0 : 1.0 - min(1.0, (1.0 - d) / u); }
float fxb_dodge(float d, float u) { return u >= 1.0 ? 1.0 : min(1.0, d / (1.0 - u)); }
float fxb_screen(float d, float u) { return 1.0 - (1.0 - d) * (1.0 - u); }
float fxb_ch(int m, float d, float u) {
  if (m == 0) return u;
  if (m == 1) return d * u;
  if (m == 2) return u <= 0.0 ? 1.0 : d / u;
  if (m == 3) return fxb_burn(d, u);
  if (m == 4) return clamp(d + u - 1.0, 0.0, 1.0);
  if (m == 5) return min(d, u);
  if (m == 7) return max(d, u);
  if (m == 8) return fxb_screen(d, u);
  if (m == 9) return fxb_dodge(d, u);
  if (m == 10) return min(1.0, d + u);
  if (m == 12) return d < 0.5 ? u * (2.0 * d) : fxb_screen(u, 2.0 * d - 1.0);
  if (m == 13) {
    if (u < 0.5) return d + (d - d * d) * (2.0 * u - 1.0);
    if (d < 0.25) return d + (2.0 * u - 1.0) * (((16.0 * d - 12.0) * d + 4.0) * d - d);
    return d + (2.0 * u - 1.0) * (sqrt(d) - d);
  }
  if (m == 14) return u < 0.5 ? d * (2.0 * u) : fxb_screen(d, 2.0 * u - 1.0);
  if (m == 15) return u < 0.5 ? fxb_burn(d, 2.0 * u) : fxb_dodge(d, 2.0 * u - 1.0);
  if (m == 16) return u < 0.5 ? clamp(d + 2.0 * u - 1.0, 0.0, 1.0) : min(1.0, d + 2.0 * u - 1.0);
  if (m == 17) return u < 0.5 ? min(2.0 * u, d) : max(2.0 * u - 1.0, d);
  return (u < 0.5 ? fxb_burn(d, 2.0 * u) : fxb_dodge(d, 2.0 * u - 1.0)) < 0.5 ? 0.0 : 1.0;
}

--- glsl
vec3 up = color.rgb;
vec3 b;
if (mode == 6 || mode == 11) {
  float ld = dot(c.rgb, vec3(0.298912, 0.586611, 0.114478));
  float lu = dot(up, vec3(0.298912, 0.586611, 0.114478));
  bool upWins = mode == 6 ? !(ld < lu) : ld < lu;
  b = upWins ? up : c.rgb;
} else {
  b = vec3(fxb_ch(mode, c.r, up.r), fxb_ch(mode, c.g, up.g), fxb_ch(mode, c.b, up.b));
}
c.rgb = mix(c.rgb, b, color.a * opacity);

--- wgsl_lib
fn fxb_burn(d: f32, u: f32) -> f32 { return select(1.0 - min(1.0, (1.0 - d) / u), 0.0, u <= 0.0); }
fn fxb_dodge(d: f32, u: f32) -> f32 { return select(min(1.0, d / (1.0 - u)), 1.0, u >= 1.0); }
fn fxb_screen(d: f32, u: f32) -> f32 { return 1.0 - (1.0 - d) * (1.0 - u); }
fn fxb_ch(m: i32, d: f32, u: f32) -> f32 {
  if (m == 0) { return u; }
  if (m == 1) { return d * u; }
  if (m == 2) { return select(d / u, 1.0, u <= 0.0); }
  if (m == 3) { return fxb_burn(d, u); }
  if (m == 4) { return clamp(d + u - 1.0, 0.0, 1.0); }
  if (m == 5) { return min(d, u); }
  if (m == 7) { return max(d, u); }
  if (m == 8) { return fxb_screen(d, u); }
  if (m == 9) { return fxb_dodge(d, u); }
  if (m == 10) { return min(1.0, d + u); }
  if (m == 12) { return select(fxb_screen(u, 2.0 * d - 1.0), u * (2.0 * d), d < 0.5); }
  if (m == 13) {
    if (u < 0.5) { return d + (d - d * d) * (2.0 * u - 1.0); }
    if (d < 0.25) { return d + (2.0 * u - 1.0) * (((16.0 * d - 12.0) * d + 4.0) * d - d); }
    return d + (2.0 * u - 1.0) * (sqrt(d) - d);
  }
  if (m == 14) { return select(fxb_screen(d, 2.0 * u - 1.0), d * (2.0 * u), u < 0.5); }
  if (m == 15) { return select(fxb_dodge(d, 2.0 * u - 1.0), fxb_burn(d, 2.0 * u), u < 0.5); }
  if (m == 16) { return select(min(1.0, d + 2.0 * u - 1.0), clamp(d + 2.0 * u - 1.0, 0.0, 1.0), u < 0.5); }
  if (m == 17) { return select(max(2.0 * u - 1.0, d), min(2.0 * u, d), u < 0.5); }
  let v = select(fxb_dodge(d, 2.0 * u - 1.0), fxb_burn(d, 2.0 * u), u < 0.5);
  return select(1.0, 0.0, v < 0.5);
}

--- wgsl
let up = color.rgb;
var b: vec3f;
if (mode == 6 || mode == 11) {
  let w = vec3f(0.298912, 0.586611, 0.114478);
  let ld = dot(c.rgb, w);
  let lu = dot(up, w);
  let upWins = select(ld < lu, !(ld < lu), mode == 6);
  b = select(c.rgb, up, upWins);
} else {
  b = vec3f(fxb_ch(mode, c.r, up.r), fxb_ch(mode, c.g, up.g), fxb_ch(mode, c.b, up.b));
}
c = vec4f(mix(c.rgb, b, color.a * opacity), c.a);

--- c_lib
static float fxb_burn( float d, float u ) { return u <= 0.f ? 0.f : 1.f - fminf( 1.f, ( 1.f - d ) / u ); }
static float fxb_dodge( float d, float u ) { return u >= 1.f ? 1.f : fminf( 1.f, d / ( 1.f - u ) ); }
static float fxb_screen( float d, float u ) { return 1.f - ( 1.f - d ) * ( 1.f - u ); }
static float fxb_clamp( float x ) { return x < 0.f ? 0.f : x > 1.f ? 1.f : x; }
static float fxb_ch( int m, float d, float u )
{
	switch ( m )
	{
	case 0: return u;
	case 1: return d * u;
	case 2: return u <= 0.f ? 1.f : d / u;
	case 3: return fxb_burn( d, u );
	case 4: return fxb_clamp( d + u - 1.f );
	case 5: return fminf( d, u );
	case 7: return fmaxf( d, u );
	case 8: return fxb_screen( d, u );
	case 9: return fxb_dodge( d, u );
	case 10: return fminf( 1.f, d + u );
	case 12: return d < 0.5f ? u * ( 2.f * d ) : fxb_screen( u, 2.f * d - 1.f );
	case 13:
		if ( u < 0.5f )
			return d + ( d - d * d ) * ( 2.f * u - 1.f );
		if ( d < 0.25f )
			return d + ( 2.f * u - 1.f ) * ( ( ( 16.f * d - 12.f ) * d + 4.f ) * d - d );
		return d + ( 2.f * u - 1.f ) * ( sqrtf( d ) - d );
	case 14: return u < 0.5f ? d * ( 2.f * u ) : fxb_screen( d, 2.f * u - 1.f );
	case 15: return u < 0.5f ? fxb_burn( d, 2.f * u ) : fxb_dodge( d, 2.f * u - 1.f );
	case 16: return u < 0.5f ? fxb_clamp( d + 2.f * u - 1.f ) : fminf( 1.f, d + 2.f * u - 1.f );
	case 17: return u < 0.5f ? fminf( 2.f * u, d ) : fmaxf( 2.f * u - 1.f, d );
	}
	return ( u < 0.5f ? fxb_burn( d, 2.f * u ) : fxb_dodge( d, 2.f * u - 1.f ) ) < 0.5f ? 0.f : 1.f;
}

--- c
float b[3];
int i;
if ( mode == 6 || mode == 11 )
{
	float ld = 0.298912f * c[0] + 0.586611f * c[1] + 0.114478f * c[2];
	float lu = 0.298912f * color[0] + 0.586611f * color[1] + 0.114478f * color[2];
	int up_wins = mode == 6 ? !( ld < lu ) : ( ld < lu );
	for ( i = 0; i < 3; i++ )
		b[i] = up_wins ? color[i] : c[i];
}
else
	for ( i = 0; i < 3; i++ )
		b[i] = fxb_ch( mode, c[i], color[i] );
for ( i = 0; i < 3; i++ )
	c[i] = c[i] + ( b[i] - c[i] ) * ( color[3] * opacity );
