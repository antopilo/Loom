"""Draws the installer's splash, splash.gif: the Home screen's animated Loom mark above the wordmark.

The mark is a port of HomeLogo (Modules/Conversations/Home/HomeLogo.cs): the same geometry, characters, wave heights and
palette. Its speeds are rounded so every motion turns a whole number of times in LOOP_SECONDS, and the GIF loops without a
jump; the palette's slow hue turn is left out for the same reason.

Velopack draws the splash at this size at 100% display scale and scales it for other scales. It paints its progress bar,
12 pixels high, across the bottom edge, over the track drawn here.

Run it again after changing the theme colors or the mark: python assets/installer/splash.py (needs Pillow).
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUTPUT = Path( __file__ ).with_name( "splash.gif" )
FONTS = Path( "C:/Windows/Fonts" )

# Theme defaults, from Core/Theme.cs.
CANVAS = "#262624"
RAISED = "#2d2d29"
BORDER = "#3b3b35"
TEXT = "#e8e6df"
MUTED = "#a09f96"
PALETTE = [ "#d5a583", "#c59bd0", "#83a8d4", "#80c2bd" ]  # Accent, AnsiMagenta, AnsiBlue, AnsiCyan.

# The splash, in pixels at 100% display scale.
WIDTH = 400
HEIGHT = 380
TRACK = 12  # Velopack's progress bar height.
SUPERSAMPLE = 3  # Drawn larger and reduced, so characters move by fractions of a pixel.

# HomeLogo's geometry and animation constants.
UNITS = 64
SCALE = 4
SIZE = UNITS * SCALE
CENTER = 32
RING_RADIUS = 20
HALF_STROKE = 3
GAP_RADIUS = 7
FILL_STEP = 5
FILL_SIZE = 9
MARGIN = 8
LIFT = 4
SWAY = 1.5
FRAME_MS = 50
SCRAMBLE_FRAMES = 3
SCRAMBLE_CHANCE = 0.04
SPREAD = 0.85
WEAVE_WIDTH = 0.09
RAMP = ":-=+*#%@"
SCRAMBLE = "!?$&<>/\\{}[]01"

# One loop: two passes of the weave's crest (4.5 seconds each in HomeLogo).
LOOP_SECONDS = 9.0
WEAVE_SECONDS = LOOP_SECONDS / 2
FRAMES = int( LOOP_SECONDS * 1000 / FRAME_MS )
TAU = math.tau


def turns( count: int ) -> float:
	"""Returns a speed in turns a second that turns exactly count times in one loop."""
	return count / LOOP_SECONDS


def parse( color: str ) -> tuple[ float, float, float ]:
	return tuple( int( color[ i : i + 2 ], 16 ) / 255 for i in ( 1, 3, 5 ) )


def mix( a, b, amount: float ):
	return tuple( x + ( y - x ) * amount for x, y in zip( a, b ) )


def to_rgb( color ) -> tuple[ int, int, int ]:
	return tuple( round( max( 0.0, min( 1.0, c ) ) * 255 ) for c in color )


def build_weft() -> list[ tuple[ float, float ] ]:
	"""Samples M8 36 C16 22 24 46 32 32 S48 18 56 28 evenly along its two curves."""
	curves = [ [ ( 8, 36 ), ( 16, 22 ), ( 24, 46 ), ( 32, 32 ) ], [ ( 32, 32 ), ( 40, 18 ), ( 48, 18 ), ( 56, 28 ) ] ]
	points = []
	for curve in curves:
		for step in range( 0 if not points else 1, 201 ):
			t = step / 200
			a, b, c, d = ( 1 - t ) ** 3, 3 * ( 1 - t ) ** 2 * t, 3 * ( 1 - t ) * t * t, t ** 3
			points.append( ( a * curve[ 0 ][ 0 ] + b * curve[ 1 ][ 0 ] + c * curve[ 2 ][ 0 ] + d * curve[ 3 ][ 0 ],
				a * curve[ 0 ][ 1 ] + b * curve[ 1 ][ 1 ] + c * curve[ 2 ][ 1 ] + d * curve[ 3 ][ 1 ] ) )
	return points


WEFT = build_weft()


def measure_weft( x: float, y: float ) -> tuple[ float, float ]:
	best, along = float( "inf" ), 0.0
	for index, ( wx, wy ) in enumerate( WEFT ):
		distance = ( wx - x ) ** 2 + ( wy - y ) ** 2
		if distance < best:
			best, along = distance, index / ( len( WEFT ) - 1 )
	return math.sqrt( best ), along


def build_points() -> list[ tuple[ float, float, bool, float ] ]:
	"""Places the characters: (x, y, is_weft, along)."""
	points = []
	count = int( SIZE / FILL_STEP )
	for row in range( count ):
		for column in range( count ):
			x, y = ( column + 0.5 ) * FILL_STEP, ( row + 0.5 ) * FILL_STEP
			weft, along = measure_weft( x / SCALE, y / SCALE )
			ring = abs( math.hypot( x / SCALE - CENTER, y / SCALE - CENTER ) - RING_RADIUS )
			if weft <= HALF_STROKE:
				points.append( ( x, y, True, along ) )
			elif weft > GAP_RADIUS and ring <= HALF_STROKE:
				points.append( ( x, y, False, 0.0 ) )
	return points


def compute_height( x: float, y: float, time: float ) -> float:
	u, v = x / SIZE, y / SIZE
	ripple = math.hypot( u - 0.5 - 0.3 * math.sin( TAU * turns( 1 ) * time ), v - 0.5 )
	return ( 0.55 * math.sin( TAU * ( 1.3 * u - turns( 3 ) * time ) + 1.7 * v )
		+ 0.3 * math.sin( TAU * ( 0.8 * v + 0.6 * u + turns( 2 ) * time ) )
		+ 0.15 * math.sin( TAU * 3 * ripple - TAU * turns( 3 ) * time ) )


def displace( x: float, y: float, time: float ) -> tuple[ float, float ]:
	sway = SWAY * math.sin( TAU * ( 0.9 * y / SIZE - turns( 2 ) * time ) + 3 * x / SIZE )
	return x + sway, y - LIFT * compute_height( x, y, time )


def compute_crest( time: float ) -> float:
	cycle = time / WEAVE_SECONDS - math.floor( time / WEAVE_SECONDS )
	return -0.25 + 1.5 * cycle * cycle * ( 3 - 2 * cycle )


def compute_fill( height: float ) -> str:
	level = max( 0.0, min( 1.0, 0.5 + 0.5 * height ) )
	return RAMP[ min( len( RAMP ) - 1, int( level * len( RAMP ) ) ) ]


def compute_noise( index: int, tick: int ) -> float:
	hash_ = ( ( index * 0x9E3779B1 ) ^ ( tick * 0x85EBCA77 ) ) & 0xFFFFFFFF
	hash_ ^= hash_ >> 15
	hash_ = ( hash_ * 0x2C1B3C6D ) & 0xFFFFFFFF
	hash_ ^= hash_ >> 12
	return ( hash_ & 0xFFFFFF ) / 0x1000000


def sample( palette, position: float ):
	scaled = ( position - math.floor( position ) ) * len( palette )
	index = int( scaled ) % len( palette )
	amount = scaled - math.floor( scaled )
	return mix( palette[ index ], palette[ ( index + 1 ) % len( palette ) ], amount * amount * ( 3 - 2 * amount ) )


def draw_frame( frame: int, points, base: Image.Image, glyph_font ) -> Image.Image:
	image = base.copy()
	draw = ImageDraw.Draw( image )
	canvas = parse( CANVAS )
	palette = [ parse( color ) for color in PALETTE ]
	time = frame * FRAME_MS / 1000
	phase = time / LOOP_SECONDS
	tick = frame // SCRAMBLE_FRAMES
	crest = compute_crest( time )
	left = ( WIDTH - ( SIZE + MARGIN * 2 ) ) / 2 + MARGIN
	top = 24 + MARGIN
	for index, ( px, py, is_weft, along ) in enumerate( points ):
		height = compute_height( px, py, time )
		x, y = displace( px, py, time )
		weave = math.exp( -2 * ( ( along - crest ) / WEAVE_WIDTH ) ** 2 ) if is_weft else 0.0
		level = max( 0.0, min( 1.0, 0.5 + 0.5 * height + 0.6 * weave ) )
		glyph = compute_fill( 1.4 * level - 0.4 )
		if compute_noise( index, tick ) < SCRAMBLE_CHANCE:
			glyph = SCRAMBLE[ int( compute_noise( index, tick + 1 ) * len( SCRAMBLE ) ) ]
		strength = max( 0.0, min( 1.0, ( 0.6 if is_weft else 0.5 ) + 0.4 * level ) )
		color = mix( canvas, sample( palette, ( px + py * 0.5 ) / ( SIZE * 1.5 ) * SPREAD + phase ), strength )
		draw.text( ( ( left + x ) * SUPERSAMPLE, ( top + y ) * SUPERSAMPLE ), glyph, fill = to_rgb( color ), font = glyph_font, anchor = "mm" )
	return image.resize( ( WIDTH, HEIGHT ), Image.Resampling.LANCZOS )


def draw_base() -> Image.Image:
	"""Draws what does not move: the card, its border, the wordmark, the caption and the progress track."""
	s = SUPERSAMPLE
	image = Image.new( "RGB", ( WIDTH * s, HEIGHT * s ), CANVAS )
	draw = ImageDraw.Draw( image )
	draw.rectangle( ( 0, ( HEIGHT - TRACK ) * s, WIDTH * s - 1, HEIGHT * s - 1 ), fill = RAISED )
	draw.rectangle( ( 0, 0, WIDTH * s - 1, HEIGHT * s - 1 ), outline = BORDER, width = s )
	wordmark = ImageFont.truetype( str( FONTS / "seguisb.ttf" ), 30 * s )
	caption = ImageFont.truetype( str( FONTS / "segoeui.ttf" ), 13 * s )
	draw.text( ( WIDTH * s / 2, 312 * s ), "Loom", fill = TEXT, font = wordmark, anchor = "mm" )
	draw.text( ( WIDTH * s / 2, 342 * s ), "Installing\u2026", fill = MUTED, font = caption, anchor = "mm" )
	return image


def main() -> None:
	points = build_points()
	base = draw_base()
	glyph_font = ImageFont.truetype( str( FONTS / "consolab.ttf" ), FILL_SIZE * SUPERSAMPLE )
	frames = [ draw_frame( frame, points, base, glyph_font ) for frame in range( FRAMES ) ]
	# One palette for every frame, so colors do not shift from frame to frame.
	sheet = Image.new( "RGB", ( WIDTH, HEIGHT * 6 ) )
	for slot, frame in enumerate( frames[ :: FRAMES // 6 ][ : 6 ] ):
		sheet.paste( frame, ( 0, HEIGHT * slot ) )
	palette = sheet.quantize( colors = 256, method = Image.Quantize.MEDIANCUT )
	indexed = [ frame.quantize( palette = palette, dither = Image.Dither.NONE ) for frame in frames ]
	indexed[ 0 ].save( OUTPUT, save_all = True, append_images = indexed[ 1 : ], duration = FRAME_MS, loop = 0, optimize = False, disposal = 1 )
	print( f"{OUTPUT}: {FRAMES} frames, {OUTPUT.stat().st_size / 1024:.0f} KB" )


if __name__ == "__main__":
	main()
