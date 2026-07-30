#!/usr/bin/env python3
from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

INSTRUMENT_OPERATOR = 41
SAMPLE_OPERATOR = 53
SF3_COMPRESSED_FLAG = 0x10
WANTED = {(0, 0), (0, 11), (0, 32), (128, 0)}


def u16(data: bytes | bytearray, offset: int) -> int:
    return struct.unpack_from('<H', data, offset)[0]


def u32(data: bytes | bytearray, offset: int) -> int:
    return struct.unpack_from('<I', data, offset)[0]


def set_u16(data: bytearray, offset: int, value: int) -> None:
    struct.pack_into('<H', data, offset, value)


def set_u32(data: bytearray, offset: int, value: int) -> None:
    struct.pack_into('<I', data, offset, value)


def text20(record: bytes | bytearray) -> str:
    return bytes(record[:20]).split(b'\0', 1)[0].decode('latin1')


def chunk(chunk_id: bytes, payload: bytes) -> bytes:
    if len(chunk_id) != 4:
        raise ValueError(chunk_id)
    out = chunk_id + struct.pack('<I', len(payload)) + payload
    if len(payload) & 1:
        out += b'\0'
    return out


def list_chunk(list_type: bytes, children: Iterable[bytes]) -> bytes:
    return chunk(b'LIST', list_type + b''.join(children))


@dataclass(frozen=True)
class ChunkRef:
    chunk_id: bytes
    payload_start: int
    payload_end: int
    raw_start: int
    raw_end: int


def iter_chunks(data: bytes, start: int, end: int) -> Iterable[ChunkRef]:
    pos = start
    while pos + 8 <= end:
        chunk_id = data[pos:pos + 4]
        size = u32(data, pos + 4)
        payload_start = pos + 8
        payload_end = payload_start + size
        raw_end = payload_end + (size & 1)
        if payload_end > end:
            raise ValueError(f'Invalid RIFF chunk {chunk_id!r}')
        yield ChunkRef(chunk_id, payload_start, payload_end, pos, raw_end)
        pos = raw_end
    if pos != end:
        raise ValueError('Trailing bytes in RIFF list')


def split_records(payload: bytes, record_size: int, name: str) -> list[bytearray]:
    if len(payload) % record_size:
        raise ValueError(f'{name} has invalid size')
    return [bytearray(payload[i:i + record_size]) for i in range(0, len(payload), record_size)]


def encode_records(records: list[bytearray]) -> bytes:
    return b''.join(bytes(record) for record in records)


def replace_info(source: bytes, info_ref: ChunkRef) -> bytes:
    children = []
    seen_name = False
    seen_software = False
    for ref in iter_chunks(source, info_ref.payload_start + 4, info_ref.payload_end):
        payload = source[ref.payload_start:ref.payload_end]
        if ref.chunk_id == b'INAM':
            payload = b'FluidR3 Jampanion\0'
            seen_name = True
        elif ref.chunk_id == b'ISFT':
            payload = b'Jampanion SF3 subset tool\0'
            seen_software = True
        children.append(chunk(ref.chunk_id, payload))
    if not seen_name:
        children.append(chunk(b'INAM', b'FluidR3 Jampanion\0'))
    if not seen_software:
        children.append(chunk(b'ISFT', b'Jampanion SF3 subset tool\0'))
    return list_chunk(b'INFO', children)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('input')
    parser.add_argument('output')
    args = parser.parse_args()

    source = Path(args.input).read_bytes()
    if source[:4] != b'RIFF' or source[8:12] != b'sfbk':
        raise ValueError('Input is not a SoundFont RIFF bank')

    top = list(iter_chunks(source, 12, len(source)))
    lists: dict[bytes, ChunkRef] = {}
    for ref in top:
        if ref.chunk_id == b'LIST':
            lists[source[ref.payload_start:ref.payload_start + 4]] = ref
    for required in (b'INFO', b'sdta', b'pdta'):
        if required not in lists:
            raise ValueError(f'Missing {required!r} list')

    pdta_ref = lists[b'pdta']
    pdta_chunks = {
        ref.chunk_id: source[ref.payload_start:ref.payload_end]
        for ref in iter_chunks(source, pdta_ref.payload_start + 4, pdta_ref.payload_end)
    }
    sdta_ref = lists[b'sdta']
    sdta_chunks = {
        ref.chunk_id: source[ref.payload_start:ref.payload_end]
        for ref in iter_chunks(source, sdta_ref.payload_start + 4, sdta_ref.payload_end)
    }
    smpl = sdta_chunks[b'smpl']

    phdr = split_records(pdta_chunks[b'phdr'], 38, 'phdr')
    pbag = split_records(pdta_chunks[b'pbag'], 4, 'pbag')
    pmod = split_records(pdta_chunks[b'pmod'], 10, 'pmod')
    pgen = split_records(pdta_chunks[b'pgen'], 4, 'pgen')
    inst = split_records(pdta_chunks[b'inst'], 22, 'inst')
    ibag = split_records(pdta_chunks[b'ibag'], 4, 'ibag')
    imod = split_records(pdta_chunks[b'imod'], 10, 'imod')
    igen = split_records(pdta_chunks[b'igen'], 4, 'igen')
    shdr = split_records(pdta_chunks[b'shdr'], 46, 'shdr')

    selected_preset_indices = [
        i for i, record in enumerate(phdr[:-1])
        if (u16(record, 22), u16(record, 20)) in WANTED
    ]
    found = {(u16(phdr[i], 22), u16(phdr[i], 20)) for i in selected_preset_indices}
    if found != WANTED:
        raise ValueError(f'Missing required presets: {sorted(WANTED - found)}')

    new_phdr: list[bytearray] = []
    new_pbag: list[bytearray] = []
    new_pmod: list[bytearray] = []
    new_pgen: list[bytearray] = []
    new_inst: list[bytearray] = []
    new_ibag: list[bytearray] = []
    new_imod: list[bytearray] = []
    new_igen: list[bytearray] = []

    instrument_map: dict[int, int] = {}
    sample_order: list[int] = []
    sample_set: set[int] = set()

    def add_sample(index: int) -> None:
        if not 0 <= index < len(shdr) - 1:
            raise ValueError(f'Invalid sample index {index}')
        if index not in sample_set:
            sample_set.add(index)
            sample_order.append(index)
        sample_type = u16(shdr[index], 44) & ~SF3_COMPRESSED_FLAG
        linked = u16(shdr[index], 42)
        # Stereo/linked samples have a meaningful link even when the index is zero.
        if linked != 0 and 0 <= linked < len(shdr) - 1 and linked not in sample_set:
            sample_set.add(linked)
            sample_order.append(linked)

    def add_instrument(old_index: int) -> int:
        if old_index in instrument_map:
            return instrument_map[old_index]
        if not 0 <= old_index < len(inst) - 1:
            raise ValueError(f'Invalid instrument index {old_index}')

        new_index = len(new_inst)
        instrument_map[old_index] = new_index
        record = bytearray(inst[old_index])
        set_u16(record, 20, len(new_ibag))
        new_inst.append(record)

        first_bag = u16(inst[old_index], 20)
        last_bag = u16(inst[old_index + 1], 20)
        for old_bag_index in range(first_bag, last_bag):
            old_bag = ibag[old_bag_index]
            next_bag = ibag[old_bag_index + 1]
            bag = bytearray(4)
            set_u16(bag, 0, len(new_igen))
            set_u16(bag, 2, len(new_imod))

            for gen_index in range(u16(old_bag, 0), u16(next_bag, 0)):
                gen = bytearray(igen[gen_index])
                if u16(gen, 0) == SAMPLE_OPERATOR:
                    add_sample(u16(gen, 2))
                new_igen.append(gen)
            for mod_index in range(u16(old_bag, 2), u16(next_bag, 2)):
                new_imod.append(bytearray(imod[mod_index]))
            new_ibag.append(bag)
        return new_index

    for old_preset_index in selected_preset_indices:
        preset = bytearray(phdr[old_preset_index])
        set_u16(preset, 24, len(new_pbag))
        new_phdr.append(preset)
        first_bag = u16(phdr[old_preset_index], 24)
        last_bag = u16(phdr[old_preset_index + 1], 24)
        for old_bag_index in range(first_bag, last_bag):
            old_bag = pbag[old_bag_index]
            next_bag = pbag[old_bag_index + 1]
            bag = bytearray(4)
            set_u16(bag, 0, len(new_pgen))
            set_u16(bag, 2, len(new_pmod))
            for gen_index in range(u16(old_bag, 0), u16(next_bag, 0)):
                gen = bytearray(pgen[gen_index])
                if u16(gen, 0) == INSTRUMENT_OPERATOR:
                    set_u16(gen, 2, add_instrument(u16(gen, 2)))
                new_pgen.append(gen)
            for mod_index in range(u16(old_bag, 2), u16(next_bag, 2)):
                new_pmod.append(bytearray(pmod[mod_index]))
            new_pbag.append(bag)

    terminal_preset = bytearray(phdr[-1])
    set_u16(terminal_preset, 24, len(new_pbag))
    new_phdr.append(terminal_preset)
    terminal_pbag = bytearray(4)
    set_u16(terminal_pbag, 0, len(new_pgen))
    set_u16(terminal_pbag, 2, len(new_pmod))
    new_pbag.append(terminal_pbag)
    new_pgen.append(bytearray(pgen[-1]))
    new_pmod.append(bytearray(pmod[-1]))

    terminal_inst = bytearray(inst[-1])
    set_u16(terminal_inst, 20, len(new_ibag))
    new_inst.append(terminal_inst)
    terminal_ibag = bytearray(4)
    set_u16(terminal_ibag, 0, len(new_igen))
    set_u16(terminal_ibag, 2, len(new_imod))
    new_ibag.append(terminal_ibag)
    new_igen.append(bytearray(igen[-1]))
    new_imod.append(bytearray(imod[-1]))

    sample_map = {old: new for new, old in enumerate(sample_order)}
    for generator in new_igen:
        if u16(generator, 0) == SAMPLE_OPERATOR:
            old_sample_index = u16(generator, 2)
            if old_sample_index not in sample_map:
                raise ValueError(f'Selected instrument references uncopied sample {old_sample_index}')
            set_u16(generator, 2, sample_map[old_sample_index])

    new_smpl = bytearray()
    new_shdr: list[bytearray] = []
    for old_index in sample_order:
        old = shdr[old_index]
        start = u32(old, 20)
        end = u32(old, 24)
        sample_type = u16(old, 44)
        if not sample_type & SF3_COMPRESSED_FLAG:
            raise ValueError(f'Expected SF3 compressed sample: {text20(old)}')
        if start > end or end > len(smpl):
            raise ValueError(f'Invalid compressed sample range: {text20(old)}')
        payload = smpl[start:end]
        if not payload.startswith(b'OggS'):
            raise ValueError(f'Compressed sample is not Ogg Vorbis: {text20(old)}')
        new_start = len(new_smpl)
        new_smpl.extend(payload)
        new_end = len(new_smpl)
        record = bytearray(old)
        set_u32(record, 20, new_start)
        set_u32(record, 24, new_end)
        # SF3 loop offsets are relative PCM sample positions and must not be shifted.
        linked = u16(old, 42)
        base_type = sample_type & ~SF3_COMPRESSED_FLAG
        if linked != 0 and linked in sample_map:
            set_u16(record, 42, sample_map[linked])
        else:
            set_u16(record, 42, 0)
        new_shdr.append(record)

    terminal_sample = bytearray(shdr[-1])
    # Keep the source bank's terminal convention (all zeroes) for maximum compatibility.
    terminal_sample[:] = b'\0' * 46
    new_shdr.append(terminal_sample)

    pdta_payloads = {
        b'phdr': encode_records(new_phdr),
        b'pbag': encode_records(new_pbag),
        b'pmod': encode_records(new_pmod),
        b'pgen': encode_records(new_pgen),
        b'inst': encode_records(new_inst),
        b'ibag': encode_records(new_ibag),
        b'imod': encode_records(new_imod),
        b'igen': encode_records(new_igen),
        b'shdr': encode_records(new_shdr),
    }
    pdta_order = [b'phdr', b'pbag', b'pmod', b'pgen', b'inst', b'ibag', b'imod', b'igen', b'shdr']
    new_pdta = list_chunk(b'pdta', [chunk(key, pdta_payloads[key]) for key in pdta_order])
    new_sdta = list_chunk(b'sdta', [chunk(b'smpl', bytes(new_smpl))])
    new_info = replace_info(source, lists[b'INFO'])

    rebuilt_top: list[bytes] = []
    for ref in top:
        if ref.chunk_id == b'LIST':
            list_type = source[ref.payload_start:ref.payload_start + 4]
            if list_type == b'INFO':
                rebuilt_top.append(new_info)
            elif list_type == b'sdta':
                rebuilt_top.append(new_sdta)
            elif list_type == b'pdta':
                rebuilt_top.append(new_pdta)
            else:
                rebuilt_top.append(source[ref.raw_start:ref.raw_end])
        else:
            rebuilt_top.append(source[ref.raw_start:ref.raw_end])

    body = b'sfbk' + b''.join(rebuilt_top)
    output = b'RIFF' + struct.pack('<I', len(body)) + body
    Path(args.output).write_bytes(output)

    print('Selected presets:')
    for record in new_phdr[:-1]:
        print(f'  bank={u16(record, 22):3d} program={u16(record, 20):3d} {text20(record)}')
    print(f'Samples: {len(shdr) - 1} -> {len(new_shdr) - 1}')
    print(f'Size: {len(source):,} -> {len(output):,} bytes')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
