// gfx2d font internals: the shape of the baked data (gfx2d_font_data.c, written by tools/font_bake.py) and what gfx2d_font.c does with it.
#ifndef STRIDE2D_GFX2D_FONT_H
#define STRIDE2D_GFX2D_FONT_H

#define GFX_BAKED_FONTS 4

typedef struct GfxGlyphRow
{
	int code, x, y, w, h;	// the character, its box in the atlas (top-left, pixels)
	int bearing_x, bearing_y; // from the pen at the baseline to the box's left edge and top edge (y UP)
	float advance;
} GfxGlyphRow;

typedef struct GfxBakedFont
{
	int size, ascent, descent;
	int width, height;
	const unsigned char* atlas; // run-length coded
	int atlas_bytes;
	const GfxGlyphRow* glyphs;
	int nglyphs;
} GfxBakedFont;

extern const GfxBakedFont gfx_baked_fonts[GFX_BAKED_FONTS];

void gfx_font_reset( void ); // forget the atlas textures (the core is shutting down and frees them)

#endif
