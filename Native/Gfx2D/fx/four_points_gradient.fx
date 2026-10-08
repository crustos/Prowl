# Four Points Gradient: Four colors, each at its own point, mixed by inverse distance. It is put over the picture (the colors may be transparent), with an opacity.
# Ported from OpenToonz, toonz/sources/stdfx/fourpointsgradientfx.cpp (BSD-3-Clause, see OPENTOONZ-LICENSE.txt). The center is a fraction of the picture (Y from the top); lengths are in pixels.
# OpenToonz's spectrum of many color keys is reduced to two colors, and a color is mixed premultiplied, as OpenToonz does.
id: 13
name: four_points_gradient
title: Four Points Gradient
group: Gradient
param: float x1 min=0 max=1 default=0.9 label="Point 1 X"
param: float y1 min=0 max=1 default=0.1 label="Point 1 Y"
param: float x2 min=0 max=1 default=0.1 label="Point 2 X"
param: float y2 min=0 max=1 default=0.1 label="Point 2 Y"
param: float x3 min=0 max=1 default=0.1 label="Point 3 X"
param: float y3 min=0 max=1 default=0.9 label="Point 3 Y"
param: float x4 min=0 max=1 default=0.9 label="Point 4 X"
param: float y4 min=0 max=1 default=0.9 label="Point 4 Y"
param: color color1 default=1,0,0,1 label="Color 1"
param: color color2 default=0,1,0,1 label="Color 2"
param: color color3 default=0,0,1,1 label="Color 3"
param: color color4 default=1,1,0,1 label="Color 4"
param: float opacity min=0 max=1 default=1 label="Opacity"

--- glsl
float w1 = 1.0 / max(length(vec2(px - x1 * pw, py - y1 * ph)), 0.001);
float w2 = 1.0 / max(length(vec2(px - x2 * pw, py - y2 * ph)), 0.001);
float w3 = 1.0 / max(length(vec2(px - x3 * pw, py - y3 * ph)), 0.001);
float w4 = 1.0 / max(length(vec2(px - x4 * pw, py - y4 * ph)), 0.001);
float ws = w1 + w2 + w3 + w4;
vec3 pm = (color1.rgb * color1.a * w1 + color2.rgb * color2.a * w2 + color3.rgb * color3.a * w3 + color4.rgb * color4.a * w4) / ws;
float al = (color1.a * w1 + color2.a * w2 + color3.a * w3 + color4.a * w4) / ws;
c = fxc_over_pm(c, pm, al, opacity);

--- wgsl
let w1 = 1.0 / max(length(vec2f(px - x1 * pw, py - y1 * ph)), 0.001);
let w2 = 1.0 / max(length(vec2f(px - x2 * pw, py - y2 * ph)), 0.001);
let w3 = 1.0 / max(length(vec2f(px - x3 * pw, py - y3 * ph)), 0.001);
let w4 = 1.0 / max(length(vec2f(px - x4 * pw, py - y4 * ph)), 0.001);
let ws = w1 + w2 + w3 + w4;
let pm = (color1.rgb * color1.a * w1 + color2.rgb * color2.a * w2 + color3.rgb * color3.a * w3 + color4.rgb * color4.a * w4) / ws;
let al = (color1.a * w1 + color2.a * w2 + color3.a * w3 + color4.a * w4) / ws;
c = fxc_over_pm(c, pm, al, opacity);

--- c
float w[4], ws, pm[3] = { 0.f, 0.f, 0.f }, al = 0.f;
const float* cols[4];
int i, k;
cols[0] = color1; cols[1] = color2; cols[2] = color3; cols[3] = color4;
{
	float ax[4], ay[4];
	ax[0] = x1; ax[1] = x2; ax[2] = x3; ax[3] = x4;
	ay[0] = y1; ay[1] = y2; ay[2] = y3; ay[3] = y4;
	for ( i = 0; i < 4; i++ )
	{
		float ex = px - ax[i] * pw, ey = py - ay[i] * ph, d = sqrtf( ex * ex + ey * ey );
		w[i] = 1.f / ( d > 0.001f ? d : 0.001f );
	}
}
ws = w[0] + w[1] + w[2] + w[3];
for ( i = 0; i < 4; i++ )
{
	for ( k = 0; k < 3; k++ )
		pm[k] += cols[i][k] * cols[i][3] * w[i];
	al += cols[i][3] * w[i];
}
for ( k = 0; k < 3; k++ )
	pm[k] /= ws;
fxc_over_pm( c, pm, al / ws, opacity );
