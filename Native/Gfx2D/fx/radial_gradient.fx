# Radial Gradient: Color 1 in the center, color 2 from the outer radius on. It is put over the picture (the colors may be transparent), with an opacity.
# Ported from OpenToonz, toonz/sources/stdfx/stdfx.cpp (RadialGradientFx) (BSD-3-Clause, see OPENTOONZ-LICENSE.txt). The center is a fraction of the picture (Y from the top); lengths are in pixels.
# OpenToonz's spectrum of many color keys is reduced to two colors, and a color is mixed premultiplied, as OpenToonz does.
id: 9
name: radial_gradient
title: Radial Gradient
group: Gradient
param: float centerX min=0 max=1 default=0.5 label="Center X"
param: float centerY min=0 max=1 default=0.5 label="Center Y"
param: float period min=1 max=10000 default=100 label="Outer radius"
param: float innerPeriod min=0 max=10000 default=0 label="Inner radius"
param: color color1 default=1,1,1,1 label="Color 1"
param: color color2 default=1,1,1,0 label="Color 2"
param: enum curve options=EaseInOut,Linear,EaseIn,EaseOut default=1 label="Curve"
param: float opacity min=0 max=1 default=1 label="Opacity"

--- glsl
float cx = centerX * pw;
float cy = centerY * ph;
float dx = px - cx;
float dy = cy - py;
float inner = innerPeriod < period ? innerPeriod / period : 0.999999;
float r = sqrt(dx * dx + dy * dy);
float t = r < period ? r / period : 1.0;
t = t <= inner ? 0.0 : (t - inner) / (1.0 - inner);
c = fxc_over(c, color1, color2, fxc_curve(curve, t), opacity);

--- wgsl
let cx = centerX * pw;
let cy = centerY * ph;
let dx = px - cx;
let dy = cy - py;
let inner = select(0.999999, innerPeriod / period, innerPeriod < period);
let r = sqrt(dx * dx + dy * dy);
var t = select(1.0, r / period, r < period);
t = select((t - inner) / (1.0 - inner), 0.0, t <= inner);
c = fxc_over(c, color1, color2, fxc_curve(curve, t), opacity);

--- c
float cx = centerX * pw;
float cy = centerY * ph;
float dx = px - cx;
float dy = cy - py;
float inner = innerPeriod < period ? innerPeriod / period : 0.999999f;
float r = sqrtf( dx * dx + dy * dy );
float t = r < period ? r / period : 1.f;
t = t <= inner ? 0.f : ( t - inner ) / ( 1.f - inner );
fxc_over( c, color1, color2, fxc_curve( curve, t ), opacity );
