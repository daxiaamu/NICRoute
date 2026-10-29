"""Generate the code-native routing brand icon using only Python's standard library."""
import math
import pathlib
import struct
import zlib

n = 64
data = []
segments = [(12,32,26,32),(26,32,40,15),(40,15,53,15),(26,32,40,49),(40,49,53,49)]
def distance(x, y, s):
    ax, ay, bx, by = s
    dx, dy = bx-ax, by-ay
    t = max(0, min(1, ((x-ax)*dx+(y-ay)*dy)/(dx*dx+dy*dy)))
    return math.hypot(x-ax-t*dx, y-ay-t*dy)
for y in range(n):
    row = bytearray([0])
    for x in range(n):
        edge = max(0, 10-x, x-53)**2 + max(0, 10-y, y-53)**2
        color = (39, 100, 231, 255) if edge <= 100 else (0, 0, 0, 0)
        if min(distance(x, y, s) for s in segments) < 2 or any(math.hypot(x-a,y-b)<4 for a,b in [(12,32),(53,15),(53,49)]):
            color = (255, 255, 255, 255)
        row.extend(color)
    data.append(row)
def chunk(name, payload):
    return struct.pack('!I', len(payload)) + name + payload + struct.pack('!I', zlib.crc32(name+payload)&0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR',struct.pack('!2I5B',n,n,8,6,0,0,0)) + chunk(b'IDAT',zlib.compress(b''.join(data))) + chunk(b'IEND',b'')
ico = struct.pack('<3H',0,1,1)+struct.pack('<4B2H2I',n,n,0,0,1,32,len(png),22)+png
(pathlib.Path(__file__).resolve().parent.parent/'src'/'NICRoute.App'/'app.ico').write_bytes(ico)
