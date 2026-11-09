"""Regenerate original test fixtures; requires fonttools 4.59.0.

These assets contain no third-party media or fonts. PGS framing follows the
public FFmpeg SUP demuxer and pgssubdec.c presentation/palette/object parsers:
https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/pgssubdec.c
https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/supdec.c
"""
from pathlib import Path
import struct
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

root = Path(__file__).resolve().parent
font = FontBuilder(1000, isTTF=True)
font.setupGlyphOrder([".notdef", "rectangle"])
font.setupCharacterMap({0xE000: "rectangle"})
empty = TTGlyphPen(None)
rectangle = TTGlyphPen(None)
rectangle.moveTo((0, 0))
rectangle.lineTo((600, 0))
rectangle.lineTo((600, 600))
rectangle.lineTo((0, 600))
rectangle.closePath()
font.setupGlyf({".notdef": empty.glyph(), "rectangle": rectangle.glyph()})
font.setupHorizontalMetrics({".notdef": (600, 0), "rectangle": (600, 0)})
font.setupHorizontalHeader(ascent=800, descent=-200)
font.setupNameTable({"familyName": "AJNFixtureRectangle", "styleName": "Regular",
                    "uniqueFontIdentifier": "AJNFixtureRectangle-1",
                    "fullName": "AJNFixtureRectangle", "psName": "AJNFixtureRectangle"})
font.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200)
font.setupPost()
font.setupMaxp()
font.font["head"].created = font.font["head"].modified = 2082844800
font.font.recalcTimestamp = False
font.save(root / "rectangle.ttf")

def be16(value):
    return struct.pack(">H", value)

def segment(seconds, kind, body):
    timestamp = round(seconds * 90000)
    return b"PG" + struct.pack(">II", timestamp, timestamp) + bytes([kind]) + be16(len(body)) + body

def presentation(number, visible):
    header = be16(480) + be16(360) + bytes([0x10]) + be16(number)
    header += bytes([0x80 if number == 0 else 0, 0, 0, int(visible)])
    return header + (be16(0) + bytes([0, 0]) + be16(100) + be16(250) if visible else b"")

# One 80x24 white bitmap, at (100,250), shown from 0.5 to 2.5 seconds.
rle = bytes([0, 0xC0, 80, 1, 0, 0]) * 24
object_data = be16(0) + bytes([0, 0xC0]) + (len(rle) + 4).to_bytes(3, "big") + be16(80) + be16(24) + rle
sup = segment(.5, 0x16, presentation(0, True))
sup += segment(.5, 0x17, bytes([1, 0]) + be16(100) + be16(250) + be16(80) + be16(24))
sup += segment(.5, 0x14, bytes([0, 0, 0, 16, 128, 128, 0, 1, 235, 128, 128, 255]))
sup += segment(.5, 0x15, object_data) + segment(.5, 0x80, b"")
sup += segment(2.5, 0x16, presentation(1, False)) + segment(2.5, 0x80, b"")
(root / "rectangle.sup").write_bytes(sup)
