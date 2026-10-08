# Spin Gradient: Color 1 to color 2 around the center, from the start angle to the end angle. It is put over the picture (the colors may be transparent), with an opacity.
# Ported from OpenToonz, toonz/sources/stdfx/iwa_spingradientfx.cpp (BSD-3-Clause, see OPENTOONZ-LICENSE.txt). The center is a fraction of the picture (Y from the top); lengths are in pixels.
# OpenToonz's spectrum of many color keys is reduced to two colors, and a color is mixed premultiplied, as OpenToonz does.
id: 12
name: spin_gradient
title: Spin Gradient
group: Gradient
param: float centerX min=0 max=1 default=0.5 label="Center X"
param: float centerY min=0 max=1 default=0.5 label="Center Y"
param: float startAngle min=-360 max=720 default=0 label="Start angle"
param: float endAngle min=-360 max=720 default=0 label="End angle"
param: color color1 default=0,0,0,1 label="Color 1"
param: color color2 default=1,1,1,1 label="Color 2"
param: enum curve options=EaseInOut,Linear,EaseIn,EaseOut default=1 label="Curve"
param: float opacity min=0 max=1 default=1 label="Opacity"

--- glsl
float cx = centerX * pw;
float cy = centerY * ph;
float dx = px - cx;
float dy = cy - py;
const float PI = 3.14159265358979;
float sa = radians(startAngle), ea = radians(endAngle);
sa = sa - 2.0 * PI * floor((sa + PI) / (2.0 * PI));
ea = ea - 2.0 * PI * floor((ea + PI) / (2.0 * PI));
float ang = atan(dy, dx);
float p = ang - sa;
if (p < 0.0) p += 2.0 * PI;
float range = ea - sa;
if (range <= 0.0) range += 2.0 * PI;
float t = range >= p ? p / range : (PI + range * 0.5 > p ? 1.0 : 0.0);
c = fxc_over(c, color1, color2, fxc_curve(curve, t), opacity);

--- wgsl
let cx = centerX * pw;
let cy = centerY * ph;
let dx = px - cx;
let dy = cy - py;
let PI = 3.14159265358979;
var sa = radians(startAngle);
var ea = radians(endAngle);
sa = sa - 2.0 * PI * floor((sa + PI) / (2.0 * PI));
ea = ea - 2.0 * PI * floor((ea + PI) / (2.0 * PI));
let ang = atan2(dy, dx);
var p = ang - sa;
if (p < 0.0) { p = p + 2.0 * PI; }
var range = ea - sa;
if (range <= 0.0) { range = range + 2.0 * PI; }
var t = 0.0;
if (range >= p) { t = p / range; } else if (PI + range * 0.5 > p) { t = 1.0; }
c = fxc_over(c, color1, color2, fxc_curve(curve, t), opacity);

--- c
float cx = centerX * pw;
float cy = centerY * ph;
float dx = px - cx;
float dy = cy - py;
const float PI = 3.14159265358979f;
float sa = startAngle * ( PI / 180.f ), ea = endAngle * ( PI / 180.f );
float ang = atan2_det( dy, dx ), pp, range, t;
sa = sa - 2.f * PI * floorf( ( sa + PI ) / ( 2.f * PI ) );
ea = ea - 2.f * PI * floorf( ( ea + PI ) / ( 2.f * PI ) );
pp = ang - sa;
if ( pp < 0.f )
	pp += 2.f * PI;
range = ea - sa;
if ( range <= 0.f )
	range += 2.f * PI;
t = range >= pp ? pp / range : ( PI + range * 0.5f > pp ? 1.f : 0.f );
fxc_over( c, color1, color2, fxc_curve( curve, t ), opacity );
