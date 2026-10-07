// gfx2d fonts: the baked bitmap fonts (gfx2d_font_data.c) as an atlas texture and a glyph table per size. See gfx2d.h for the API.
//
// The atlas is made on first use, as an ordinary texture of the renderer (alpha is the glyph's coverage, colour white, so a vertex's tint colours the text), and is
// freed with the others when the renderer shuts down. A string is laid out by the game (the UI library) from the advances given here.
#include <stdlib.h>
#include <string.h>

#include "gfx2d_font.h"
#include "gfx2d.h"

static int g_tex[GFX_BAKED_FONTS];

void gfx_font_reset( void )
{
	memset( g_tex, 0, sizeof g_tex );
}

int gfx_font_count( void )
{
	return GFX_BAKED_FONTS;
}

int gfx_font_size( int index )
{
	return index >= 0 && index < GFX_BAKED_FONTS ? gfx_baked_fonts[index].size : 0;
}

int gfx_font_texture( int index )
{
	const GfxBakedFont* f;
	uint8_t* rgba;
	size_t i, n, o = 0;
	int id;
	if ( index < 0 || index >= GFX_BAKED_FONTS )
		return 0;
	if ( g_tex[index] )
		return g_tex[index];
	f = &gfx_baked_fonts[index];
	n = (size_t)f->width * (size_t)f->height;
	rgba = (uint8_t*)malloc( n * 4 );
	if ( rgba == NULL )
		return 0;
	for ( i = 0; i < (size_t)f->atlas_bytes && o < n; i++ ) // the run-length code: 0, count of zeros; or a literal coverage byte
	{
		unsigned char b = f->atlas[i];
		size_t run = 1, k;
		if ( b == 0 )
			run = f->atlas[++i];
		for ( k = 0; k < run && o < n; k++, o++ )
		{
			rgba[o * 4] = rgba[o * 4 + 1] = rgba[o * 4 + 2] = 255;
			rgba[o * 4 + 3] = b;
		}
	}
	id = gfx_texture( f->width, f->height, GFX_FILTER_LINEAR, rgba );
	free( rgba );
	g_tex[index] = id;
	return id;
}

int gfx_font_metrics( int index, float* out4 )
{
	const GfxBakedFont* f;
	if ( index < 0 || index >= GFX_BAKED_FONTS || out4 == NULL )
		return 0;
	f = &gfx_baked_fonts[index];
	out4[0] = (float)f->ascent;
	out4[1] = (float)f->descent;
	out4[2] = (float)( f->ascent + f->descent ); // the distance between two lines
	out4[3] = (float)f->size;
	return 1;
}

static const GfxGlyphRow* find( const GfxBakedFont* f, int code )
{
	int lo = 0, hi = f->nglyphs - 1; // the table is in code order
	while ( lo <= hi )
	{
		int mid = ( lo + hi ) / 2;
		if ( f->glyphs[mid].code == code )
			return &f->glyphs[mid];
		if ( f->glyphs[mid].code < code )
			lo = mid + 1;
		else
			hi = mid - 1;
	}
	return NULL;
}

int gfx_font_glyph( int index, int codepoint, float* out9 )
{
	const GfxBakedFont* f;
	const GfxGlyphRow* g;
	int found = 1;
	if ( index < 0 || index >= GFX_BAKED_FONTS || out9 == NULL )
		return 0;
	f = &gfx_baked_fonts[index];
	g = find( f, codepoint );
	if ( g == NULL )
	{
		g = find( f, '?' );
		found = 0;
	}
	out9[0] = (float)g->x / (float)f->width;
	out9[1] = (float)g->y / (float)f->height;
	out9[2] = (float)( g->x + g->w ) / (float)f->width;
	out9[3] = (float)( g->y + g->h ) / (float)f->height;
	out9[4] = (float)g->w;
	out9[5] = (float)g->h;
	out9[6] = (float)g->bearing_x;
	out9[7] = (float)g->bearing_y;
	out9[8] = g->advance;
	return found;
}
