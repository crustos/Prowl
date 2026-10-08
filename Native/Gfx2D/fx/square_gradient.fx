# Square Gradient: Color 1 in the center to color 2 where |x| + |y| reaches the size (OpenToonz calls this one "square"; its shape is a diamond). It is put over the picture (the colors may be transparent), with an opacity.
# Ported from OpenToonz, toonz/sources/stdfx/squaregradientfx.cpp (BSD-3-Clause, see OPENTOONZ-LICENSE.txt). The center is a fraction of the picture (Y from the top); lengths are in pixels.
# OpenToonz's spectrum of many color keys is reduced to two colors, and a color is mixed premultiplied, as OpenToonz does.
id: 10
name: square_gradient
title: Square Gradient
group: Gradient
param: float centerX min=0 max=1 default=0.5 label="Center X"
param: float centerY min=0 max=1 default=0.5 label="Center Y"
param: float size min=1 max=10000 default=200 label="Size"
param: color color1 default=1,1,1,1 label="Color 1"
param: color color2 default=0,0,0,1 label="Color 2"
param: float opacity min=0 max=1 default=1 label="Opacity"

--- glsl
float cx = centerX * pw;
float cy = centerY * ph;
float dx = px - cx;
float dy = cy - py;
float s = (abs(dx) + abs(dy)) / size;
float t = s < 1.0 ? s : 1.0;
c = fxc_over(c, color1, color2, t, opacity);

--- wgsl
let cx = centerX * pw;
let cy = centerY * ph;
let dx = px - cx;
let dy = cy - py;
let s = (abs(dx) + abs(dy)) / size;
let t = select(1.0, s, s < 1.0);
c = fxc_over(c, color1, color2, t, opacity);

--- c
float cx = centerX * pw;
float cy = centerY * ph;
float dx = px - cx;
float dy = cy - py;
float s = ( fabsf( dx ) + fabsf( dy ) ) / size;
float t = s < 1.f ? s : 1.f;
fxc_over( c, color1, color2, t, opacity );
