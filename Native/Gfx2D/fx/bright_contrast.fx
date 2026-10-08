# Brightness / contrast. Contrast scales each channel's distance from mid grey (0: unchanged, 1: four times as far, -1: flat grey); brightness then
# shifts every channel. The result is clamped when it is stored, so the bodies below do not clamp.
id: 1
name: bright_contrast
title: Brightness / Contrast
group: Color
param: float brightness min=-1 max=1 default=0 label="Brightness"
param: float contrast min=-1 max=1 default=0 label="Contrast"

--- glsl
float k = contrast < 0.0 ? 1.0 + contrast : 1.0 + 3.0 * contrast;
c.rgb = (c.rgb - 0.5) * k + 0.5 + brightness;

--- wgsl
let k = select(1.0 + 3.0 * contrast, 1.0 + contrast, contrast < 0.0);
c = vec4f((c.rgb - 0.5) * k + 0.5 + brightness, c.a);

--- c
float k = contrast < 0.0f ? 1.0f + contrast : 1.0f + 3.0f * contrast;
int i;
for ( i = 0; i < 3; i++ )
	c[i] = ( c[i] - 0.5f ) * k + 0.5f + brightness;
