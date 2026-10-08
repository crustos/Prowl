# Tint: blends the picture with itself multiplied by a colour. amount 0 leaves it alone, 1 multiplies it by the colour completely.
# (It also exercises a colour parameter, which every backend must read from the same four floats.)
id: 2
name: tint
title: Tint
group: Color
param: color color default=1,0.5,0.1,1 label="Colour"
param: float amount min=0 max=1 default=0.5 label="Amount"

--- glsl
c.rgb = c.rgb * (1.0 - amount) + c.rgb * color.rgb * amount;

--- wgsl
c = vec4f(c.rgb * (1.0 - amount) + c.rgb * color.rgb * amount, c.a);

--- c
int i;
for ( i = 0; i < 3; i++ )
	c[i] = c[i] * ( 1.0f - amount ) + c[i] * color[i] * amount;
