# Linear Gradient: A band of color 1 to color 2 across the picture, at an angle, optionally waved. It is put over the picture (the colors may be transparent), with an opacity.
# Ported from OpenToonz, toonz/sources/stdfx/stdfx.cpp (LinearGradientFx) (BSD-3-Clause, see OPENTOONZ-LICENSE.txt). The center is a fraction of the picture (Y from the top); lengths are in pixels.
# OpenToonz's spectrum of many color keys is reduced to two colors, and a color is mixed premultiplied, as OpenToonz does.
id: 8
name: linear_gradient
title: Linear Gradient
group: Gradient
param: float centerX min=0 max=1 default=0.5 label="Center X"
param: float centerY min=0 max=1 default=0.5 label="Center Y"
param: float angle min=-360 max=360 default=0 label="Angle"
param: float period min=1 max=10000 default=200 label="Period"
param: float waveAmp min=0 max=1000 default=0 label="Wave amplitude"
param: float waveFreq min=0 max=1 default=0 label="Wave frequency"
param: float wavePhase min=-6.3 max=6.3 default=0 label="Wave phase"
param: float cycle min=-10000 max=10000 default=0 label="Offset"
param: color color1 default=0,0,0,1 label="Color 1"
param: color color2 default=1,1,1,1 label="Color 2"
param: enum curve options=EaseInOut,Linear,EaseIn,EaseOut default=0 label="Curve"
param: float opacity min=0 max=1 default=1 label="Opacity"

--- glsl
float cx = centerX * pw;
float cy = centerY * ph;
float dx = px - cx;
float dy = cy - py;
float a = radians(angle);
float xr = dx * cos(a) + dy * sin(a);
float yr = -dx * sin(a) + dy * cos(a);
float shift = waveAmp != 0.0 ? waveAmp * sin(waveFreq * yr + wavePhase) : 0.0;
float rad = xr + shift;
float maxR = period * 0.5;
float t = 1.0;
if (abs(rad) < maxR) { t = (rad + maxR + cycle) / period; t -= floor(t); }
else if (rad < 0.0) t = 0.0;
c = fxc_over(c, color1, color2, fxc_curve(curve, t), opacity);

--- wgsl
let cx = centerX * pw;
let cy = centerY * ph;
let dx = px - cx;
let dy = cy - py;
let a = radians(angle);
let xr = dx * cos(a) + dy * sin(a);
let yr = -dx * sin(a) + dy * cos(a);
var shift = 0.0;
if (waveAmp != 0.0) { shift = waveAmp * sin(waveFreq * yr + wavePhase); }
let rad = xr + shift;
let maxR = period * 0.5;
var t = 1.0;
if (abs(rad) < maxR) { t = (rad + maxR + cycle) / period; t = t - floor(t); }
else if (rad < 0.0) { t = 0.0; }
c = fxc_over(c, color1, color2, fxc_curve(curve, t), opacity);

--- c
float cx = centerX * pw;
float cy = centerY * ph;
float dx = px - cx;
float dy = cy - py;
float a = angle * 0.017453292519943295f;
float ca = cos_det( a ), sa = sin_det( a );
float xr = dx * ca + dy * sa;
float yr = -dx * sa + dy * ca;
float shift = waveAmp != 0.f ? waveAmp * sin_det( waveFreq * yr + wavePhase ) : 0.f;
float rad = xr + shift;
float maxR = period * 0.5f;
float t = 1.f;
if ( fabsf( rad ) < maxR )
{
	t = ( rad + maxR + cycle ) / period;
	t -= floorf( t );
}
else if ( rad < 0.f )
	t = 0.f;
fxc_over( c, color1, color2, fxc_curve( curve, t ), opacity );
