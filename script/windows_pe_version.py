"""Read a Windows PE's RT_VERSION resource without executing the file."""
import struct
from pathlib import Path

def read_version(path):
    data = Path(path).read_bytes()
    u16 = lambda offset: struct.unpack_from('<H', data, offset)[0]
    u32 = lambda offset: struct.unpack_from('<I', data, offset)[0]
    if data[:2] != b'MZ': raise ValueError('Not a Windows PE')
    pe = u32(0x3c)
    if data[pe:pe + 4] != b'PE\0\0': raise ValueError('Invalid PE header')
    sections, optional_size = u16(pe + 6), u16(pe + 20)
    optional = pe + 24
    directory = optional + (112 if u16(optional) == 0x20b else 96)
    resource_rva = u32(directory + 16)
    section_table = optional + optional_size
    def address(rva):
        for i in range(sections):
            at = section_table + i * 40
            virtual_size, virtual_address, raw_size, raw_address = struct.unpack_from('<IIII', data, at + 8)
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_address + rva - virtual_address
        raise ValueError('Version resource is outside PE sections')
    base = address(resource_rva)
    def entries(relative):
        start = base + relative
        return [struct.unpack_from('<II', data, start + 16 + i * 8)
                for i in range(u16(start + 12) + u16(start + 14))]
    target = next(offset for name, offset in entries(0) if name == 16)
    while target & 0x80000000:
        target = entries(target & 0x7fffffff)[0][1]
    value_rva, size = struct.unpack_from('<II', data, base + target)
    start = address(value_rva)
    if start + size > len(data): raise ValueError('Truncated version resource')
    strings = {}
    fixed = None
    def block(offset, limit):
        nonlocal fixed
        length, value_length, kind = struct.unpack_from('<HHH', data, offset)
        if length < 6 or offset + length > limit: raise ValueError('Invalid version block')
        key_end = offset + 6
        while data[key_end:key_end + 2] != b'\0\0':
            key_end += 2
            if key_end + 2 > offset + length: raise ValueError('Invalid version key')
        key = data[offset + 6:key_end].decode('utf-16le')
        value_start = (key_end + 2 + 3) & ~3
        value_size = value_length * 2 if kind == 1 else value_length
        if key == 'VS_VERSION_INFO':
            fixed = struct.unpack_from('<13I', data, value_start)
            if fixed[0] != 0xfeef04bd: raise ValueError('Invalid fixed version signature')
        elif kind == 1 and value_length:
            strings[key] = data[value_start:value_start + value_size].decode('utf-16le').rstrip('\0')
        child = (value_start + value_size + 3) & ~3
        while child + 6 <= offset + length and u16(child):
            consumed = block(child, offset + length)
            child = (child + consumed + 3) & ~3
        return length
    block(start, start + size)
    major, minor = fixed[2] >> 16, fixed[2] & 0xffff
    build, revision = fixed[3] >> 16, fixed[3] & 0xffff
    return dict(path=str(path), assemblyVersion=None, fileVersion=f'{major}.{minor}.{build}.{revision}',
                productVersion=strings.get('ProductVersion'))
