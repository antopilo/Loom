"""Builds LoomFluentIcons.ttf: Fluent System Icons outlines at the Segoe Fluent Icons code points Loom draws.

Linux uses the font when Segoe Fluent Icons is not installed. Each icon is centered in the em and scaled so its longest ink
span is the given share of it.

Run it again after changing ICONS: python assets/fonts/build-icon-font.py (needs fontTools).
"""

import argparse
import json
from io import BytesIO
from pathlib import Path
from urllib.request import urlopen

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont

UPSTREAM = "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/a3de63245b4fa49c30e20d9838593a105affe774/fonts/"
FONT_DIR = Path( __file__ ).resolve().parent
FAMILY = "Loom Fluent Icons"
EM = 2048

# Segoe code point: (Segoe name, Fluent icon with its size and style, longest ink span in % of em).
ICONS = {
	0xE70D: ( "ChevronDown", "chevron_down_16_regular", 75 ),
	0xE70F: ( "Edit", "edit_16_regular", 98 ),
	0xE710: ( "Add", "add_16_regular", 81 ),
	0xE711: ( "Cancel", "dismiss_16_regular", 69 ),
	0xE712: ( "More", "more_horizontal_16_regular", 78 ),
	0xE713: ( "Settings", "settings_16_regular", 98 ),
	0xE715: ( "Mail", "mail_16_regular", 98 ),
	0xE716: ( "People", "people_16_regular", 98 ),
	0xE718: ( "Pin", "pin_16_regular", 98 ),
	0xE71B: ( "Link", "link_16_regular", 98 ),
	0xE721: ( "Search", "search_16_regular", 88 ),
	0xE724: ( "Send", "send_16_regular", 98 ),
	0xE72A: ( "Forward", "arrow_right_16_regular", 98 ),
	0xE72B: ( "Back", "arrow_left_16_regular", 98 ),
	0xE72C: ( "Refresh", "arrow_clockwise_16_regular", 98 ),
	0xE73E: ( "CheckMark", "checkmark_16_regular", 75 ),
	0xE740: ( "FullScreen", "arrow_maximize_16_regular", 88 ),
	0xE74D: ( "Delete", "delete_16_regular", 98 ),
	0xE74E: ( "Save", "save_16_regular", 88 ),
	0xE751: ( "ReturnKey", "arrow_turn_down_left_20_regular", 88 ),
	0xE756: ( "CommandPrompt", "window_console_20_regular", 88 ),
	0xE76C: ( "ChevronRight", "chevron_right_16_regular", 75 ),
	0xE774: ( "Globe", "globe_16_regular", 98 ),
	0xE77A: ( "Unpin", "pin_off_16_regular", 98 ),
	0xE790: ( "Color", "color_16_regular", 98 ),
	0xE7B8: ( "Package", "archive_16_regular", 98 ),
	0xE7C3: ( "Page", "document_16_regular", 98 ),
	0xE7E8: ( "PowerButton", "power_20_regular", 98 ),
	0xE7F4: ( "TVMonitor", "desktop_16_regular", 98 ),
	0xE80F: ( "Home", "home_16_regular", 94 ),
	0xE81C: ( "History", "history_16_regular", 98 ),
	0xE823: ( "Recent", "clock_16_regular", 94 ),
	0xE838: ( "FolderOpen", "folder_open_16_regular", 98 ),
	0xE897: ( "Help", "question_16_regular", 88 ),
	0xE8A0: ( "OpenPane", "panel_right_expand_20_regular", 98 ),
	0xE8A5: ( "Document", "document_16_regular", 98 ),
	0xE8A7: ( "OpenInNewWindow", "open_16_regular", 88 ),
	0xE8A9: ( "ViewAll", "grid_16_regular", 88 ),
	0xE8B7: ( "Folder", "folder_16_regular", 98 ),
	0xE8BB: ( "ChromeClose", "dismiss_16_regular", 100 ),
	0xE8BD: ( "Message", "chat_16_regular", 98 ),
	0xE8F2: ( "ChatBubbles", "chat_multiple_16_regular", 98 ),
	0xE8F4: ( "NewFolder", "folder_add_16_regular", 98 ),
	0xE8FD: ( "BulletedList", "text_bullet_list_16_regular", 98 ),
	0xE90C: ( "DockLeft", "panel_left_16_regular", 98 ),
	0xE90F: ( "Repair", "wrench_16_regular", 98 ),
	0xE91F: ( "FullCircleMask", "circle_16_filled", 94 ),
	0xE921: ( "ChromeMinimize", "subtract_16_regular", 100 ),
	0xE922: ( "ChromeMaximize", "square_16_regular", 100 ),
	0xE923: ( "ChromeRestore", "square_multiple_16_regular", 100 ),
	0xE943: ( "Code", "braces_16_regular", 98 ),
	0xE945: ( "LightningBolt", "flash_16_regular", 98 ),
	0xE946: ( "Info", "info_16_regular", 94 ),
	0xE99A: ( "Robot", "bot_16_regular", 98 ),
	0xE9CE: ( "Unknown", "question_circle_16_regular", 94 ),
	0xE9D5: ( "CheckList", "task_list_ltr_20_regular", 98 ),
	0xE9F9: ( "ReportDocument", "document_data_16_regular", 98 ),
	0xEA80: ( "Lightbulb", "lightbulb_16_regular", 98 ),
	0xEA8F: ( "Ringer", "alert_16_regular", 97 ),
	0xEA99: ( "Broom", "broom_16_regular", 98 ),
	0xEC26: ( "HomeGroup", "molecule_16_regular", 98 ),
	0xECA5: ( "Tiles", "board_16_regular", 88 ),
	0xF003: ( "Relationship", "branch_fork_16_regular", 98 ),
}


def read_source( source_dir: Path | None, style: str, extension: str ) -> bytes:
	"""Reads one upstream font or its name map, from source_dir or the pinned upstream commit."""
	filename = f"FluentSystemIcons-{style.capitalize()}.{extension}"
	if source_dir is not None:
		return ( source_dir / filename ).read_bytes()
	with urlopen( UPSTREAM + filename, timeout = 30 ) as response:
		return response.read()


def build_font( sources: dict[ str, tuple[ TTFont, dict[ str, int ] ] ], output: Path ) -> None:
	cmap = {}
	glyphs = { ".notdef": TTGlyphPen( None ).glyph() }
	for codepoint, ( _, icon, span ) in ICONS.items():
		source, names = sources[ icon.rsplit( "_", 1 )[ 1 ] ]
		source_glyphs = source.getGlyphSet()
		outline = DecomposingRecordingPen( source_glyphs )
		source_glyphs[ source.getBestCmap()[ names[ f"ic_fluent_{icon}" ] ] ].draw( outline )
		bounds = BoundsPen( None )
		outline.replay( bounds )
		left, bottom, right, top = bounds.bounds
		scale = EM * span / ( 100 * max( right - left, top - bottom ) )
		center_y = 0.55 if codepoint == 0xE921 else 0.5
		x = EM / 2 - ( left + right ) * scale / 2
		y = EM * center_y - ( bottom + top ) * scale / 2
		pen = TTGlyphPen( None )
		outline.replay( TransformPen( pen, ( scale, 0, 0, scale, x, y ) ) )
		glyph_name = f"uni{codepoint:04X}"
		glyphs[ glyph_name ] = pen.glyph()
		cmap[ codepoint ] = glyph_name

	head = sources[ "regular" ][ 0 ][ "head" ]
	font = FontBuilder( EM )
	font.font[ "head" ].created = head.created
	font.font[ "head" ].modified = head.modified
	font.setupGlyphOrder( list( glyphs ) )
	font.setupCharacterMap( cmap )
	font.setupGlyf( glyphs )
	font.setupHorizontalMetrics( { name: ( EM, glyph.xMin ) for name, glyph in glyphs.items() } )
	font.setupHorizontalHeader( ascent = EM, descent = 0 )
	font.setupOS2( sTypoAscender = EM, sTypoDescender = 0, usWinAscent = EM, usWinDescent = 0 )
	font.setupNameTable( {
		"familyName": FAMILY,
		"styleName": "Regular",
		"uniqueFontIdentifier": FAMILY,
		"fullName": FAMILY,
		"psName": "LoomFluentIcons",
		"licenseDescription": ( FONT_DIR / "MIT-fluent-system-icons.txt" ).read_text( encoding = "utf-8" ),
	} )
	font.setupPost( isFixedPitch = 1 )
	font.save( output )


def main() -> None:
	parser = argparse.ArgumentParser( description = __doc__ )
	parser.add_argument( "--source-dir", type = Path, help = "a folder with the upstream TTF and JSON files, instead of downloading them" )
	parser.add_argument( "--output", type = Path, default = FONT_DIR / "LoomFluentIcons.ttf" )
	args = parser.parse_args()
	styles = { icon.rsplit( "_", 1 )[ 1 ] for _, icon, _ in ICONS.values() }
	build_font( { style: ( TTFont( BytesIO( read_source( args.source_dir, style, "ttf" ) ) ), json.loads( read_source( args.source_dir, style, "json" ) ) )
		for style in styles }, args.output )
	print( f"{args.output}: {len( ICONS )} icons, {args.output.stat().st_size / 1024:.0f} KB" )


if __name__ == "__main__":
	main()
