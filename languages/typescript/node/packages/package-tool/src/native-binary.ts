import { createReadStream } from "node:fs";
import { open, stat, type FileHandle } from "node:fs/promises";
import type { SunderRid } from "./model";

export type PackageNativeRid = SunderRid;

export type NativeBinaryKind =
  | "opaque"
  | "shared-library"
  | "executable"
  | "managed-assembly"
  | "toolchain"
  | "invalid";

export interface NativeBinaryInspection {
  readonly format: "opaque" | "script" | "pe" | "coff" | "elf" | "mach-o" | "archive" | "llvm-bitcode" | "pdb";
  readonly kind: NativeBinaryKind;
  readonly architectures: readonly string[];
  readonly detail: string;
}

const PE_SIGNATURE = Buffer.from([0x50, 0x45, 0x00, 0x00]);
const ELF_SIGNATURE = Buffer.from([0x7f, 0x45, 0x4c, 0x46]);
const ARCHIVE_SIGNATURE = Buffer.from("!<arch>\n", "ascii");
const PDB_SIGNATURE = Buffer.from("Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0\0", "binary");
const MAXIMUM_BINARY_HEADER_BYTES = 16 * 1024 * 1024;

const RID_BINARY_PROFILE: Readonly<Record<PackageNativeRid, {
  readonly format: "pe" | "elf" | "mach-o";
  readonly architecture: string;
}>> = {
  "win-x64": { format: "pe", architecture: "x64" },
  "win-arm64": { format: "pe", architecture: "arm64" },
  "linux-x64": { format: "elf", architecture: "x64" },
  "linux-arm64": { format: "elf", architecture: "arm64" },
  "osx-x64": { format: "mach-o", architecture: "x64" },
  "osx-arm64": { format: "mach-o", architecture: "arm64" },
};

const RUNTIME_FINGERPRINTS = [
  {
    label: "Node runtime",
    requiredMatches: 3,
    values: [
      "NODE_OPTIONS",
      "NODE_EXTRA_CA_CERTS",
      "node::Start",
      "process.binding",
      "Node.js JavaScript Runtime",
      "node:internal/bootstrap",
    ],
  },
  {
    label: ".NET execution runtime",
    requiredMatches: 2,
    values: [
      "System.Private.CoreLib",
      "Microsoft.NETCore.App",
      "coreclr_initialize",
      "coreclr_execute_assembly",
      "hostfxr_main",
      "hostpolicy",
      "DOTNET_ROOT",
    ],
  },
] as const;

export async function inspectNativeBinary(path: string, rid: PackageNativeRid): Promise<NativeBinaryInspection> {
  const fileSize = (await stat(path)).size;
  const handle = await open(path, "r");
  try {
    const prefix = await readAvailable(handle, 0, Math.min(fileSize, Math.max(PDB_SIGNATURE.length, 8)));
    if (prefix.length >= 2 && prefix[0] === 0x23 && prefix[1] === 0x21) {
      return inspection("script", "executable", [], "a shebang script");
    }
    if (prefix.length >= ARCHIVE_SIGNATURE.length
      && prefix.subarray(0, ARCHIVE_SIGNATURE.length).equals(ARCHIVE_SIGNATURE)) {
      return inspection("archive", "toolchain", [], "a static/object archive");
    }
    if (prefix.length >= PDB_SIGNATURE.length
      && prefix.subarray(0, PDB_SIGNATURE.length).equals(PDB_SIGNATURE)) {
      return inspection("pdb", "toolchain", [], "a program database");
    }
    if (prefix.length >= 4 && (prefix.subarray(0, 4).equals(Buffer.from([0x42, 0x43, 0xc0, 0xde]))
      || prefix.subarray(0, 4).equals(Buffer.from([0xde, 0xc0, 0x17, 0x0b])))) {
      return inspection("llvm-bitcode", "toolchain", [], "LLVM bitcode");
    }

    let result: NativeBinaryInspection | null = null;
    try {
      if (prefix.length >= 2 && prefix[0] === 0x4d && prefix[1] === 0x5a) {
        result = await inspectPortableExecutable(handle, fileSize);
      } else if (prefix.length >= ELF_SIGNATURE.length
        && prefix.subarray(0, ELF_SIGNATURE.length).equals(ELF_SIGNATURE)) {
        result = await inspectElf(handle, fileSize);
      } else if (prefix.length >= 4 && machMagic(prefix.subarray(0, 4)) !== null) {
        result = await inspectMach(handle, fileSize, prefix.subarray(0, 4));
      } else {
        result = await inspectCoffObject(handle, fileSize, prefix);
      }
    } catch (error) {
      if (error instanceof InvalidBinaryError) {
        return inspection(error.format, "invalid", [], error.message);
      }
      throw error;
    }
    if (result === null || result.kind !== "shared-library") return result ?? opaqueInspection();

    const expected = RID_BINARY_PROFILE[rid];
    if (result.format !== expected.format) {
      return inspection(
        result.format,
        "invalid",
        result.architectures,
        `${result.format} content is foreign to ${rid}`,
      );
    }
    if (!result.architectures.includes(expected.architecture)) {
      return inspection(
        result.format,
        "invalid",
        result.architectures,
        `${result.format} content does not contain the ${expected.architecture} architecture required by ${rid}`,
      );
    }
    return result;
  } finally {
    await handle.close();
  }
}

export async function detectRuntimeBinaryFingerprint(path: string): Promise<string | null> {
  const groups = RUNTIME_FINGERPRINTS.map((group) => ({
    ...group,
    matches: new Set<number>(),
    patterns: group.values.flatMap((value, index) => [
      { index, bytes: Buffer.from(value, "ascii") },
      { index, bytes: Buffer.from(value, "utf16le") },
    ]),
  }));
  const overlapBytes = Math.max(...groups.flatMap((group) => group.patterns.map((pattern) => pattern.bytes.length))) - 1;
  let tail = Buffer.alloc(0);
  for await (const value of createReadStream(path, { highWaterMark: 1024 * 1024 })) {
    const chunk = Buffer.concat([tail, value as Buffer]);
    for (const group of groups) {
      for (const pattern of group.patterns) {
        if (!group.matches.has(pattern.index) && chunk.indexOf(pattern.bytes) >= 0) {
          group.matches.add(pattern.index);
        }
      }
      if (group.matches.size >= group.requiredMatches) return group.label;
    }
    tail = chunk.subarray(Math.max(0, chunk.length - overlapBytes));
  }
  return null;
}

async function inspectPortableExecutable(handle: FileHandle, fileSize: number): Promise<NativeBinaryInspection> {
  const dosHeader = await readExact(handle, 0, 64, fileSize, "pe");
  const peOffset = dosHeader.readUInt32LE(0x3c);
  if (peOffset < 64 || peOffset > MAXIMUM_BINARY_HEADER_BYTES) {
    throw new InvalidBinaryError("pe", "PE content has an invalid image-header offset");
  }
  const signatureAndHeader = await readExact(handle, peOffset, 24, fileSize, "pe");
  if (!signatureAndHeader.subarray(0, 4).equals(PE_SIGNATURE)) {
    throw new InvalidBinaryError("pe", "MZ content has no valid PE signature");
  }
  const machine = signatureAndHeader.readUInt16LE(4);
  const architecture = peArchitecture(machine);
  const sectionCount = signatureAndHeader.readUInt16LE(6);
  const optionalHeaderBytes = signatureAndHeader.readUInt16LE(20);
  const characteristics = signatureAndHeader.readUInt16LE(22);
  if (architecture === null || sectionCount === 0 || sectionCount > 96
    || optionalHeaderBytes < 96 || optionalHeaderBytes > 4096
    || (characteristics & 0x0002) === 0) {
    throw new InvalidBinaryError("pe", "PE content has invalid or unsupported image metadata");
  }
  const optionalHeader = await readExact(handle, peOffset + 24, optionalHeaderBytes, fileSize, "pe");
  const optionalMagic = optionalHeader.readUInt16LE(0);
  const dataDirectoryOffset = optionalMagic === 0x20b ? 112 : optionalMagic === 0x10b ? 96 : 0;
  const directoryCountOffset = optionalMagic === 0x20b ? 108 : optionalMagic === 0x10b ? 92 : 0;
  if (dataDirectoryOffset === 0 || optionalHeaderBytes < dataDirectoryOffset) {
    throw new InvalidBinaryError("pe", "PE content has an invalid optional-header format");
  }
  const sectionTableEnd = peOffset + 24 + optionalHeaderBytes + sectionCount * 40;
  if (sectionTableEnd > fileSize) throw new InvalidBinaryError("pe", "PE content has a truncated section table");

  const directoryCount = optionalHeader.readUInt32LE(directoryCountOffset);
  if (directoryCount > Math.floor((optionalHeader.length - dataDirectoryOffset) / 8)) {
    throw new InvalidBinaryError("pe", "PE content declares data directories outside its optional header");
  }
  let hasClrHeader = false;
  if (directoryCount > 14) {
    const clrDirectoryOffset = dataDirectoryOffset + 14 * 8;
    if (clrDirectoryOffset + 8 > optionalHeader.length) {
      throw new InvalidBinaryError("pe", "PE content has a truncated CLR data directory");
    }
    hasClrHeader = optionalHeader.readUInt32LE(clrDirectoryOffset) !== 0
      || optionalHeader.readUInt32LE(clrDirectoryOffset + 4) !== 0;
  }
  const sectionTable = await readExact(
    handle,
    peOffset + 24 + optionalHeaderBytes,
    sectionCount * 40,
    fileSize,
    "pe",
  );
  for (let index = 0; index < sectionCount; index++) {
    const sectionOffset = index * 40;
    const rawBytes = sectionTable.readUInt32LE(sectionOffset + 16);
    const rawOffset = sectionTable.readUInt32LE(sectionOffset + 20);
    if (rawBytes > 0 && (rawOffset === 0 || rawOffset + rawBytes > fileSize)) {
      throw new InvalidBinaryError("pe", "PE content has a section outside the file");
    }
  }
  if (hasClrHeader) {
    return inspection("pe", "managed-assembly", [architecture], "a managed PE/CLR assembly");
  }
  return (characteristics & 0x2000) === 0
    ? inspection("pe", "executable", [architecture], "a PE executable image")
    : inspection("pe", "shared-library", [architecture], "a native PE shared library");
}

async function inspectCoffObject(
  handle: FileHandle,
  fileSize: number,
  prefix: Buffer,
): Promise<NativeBinaryInspection | null> {
  if (prefix.length < 2 || peArchitecture(prefix.readUInt16LE(0)) === null || fileSize < 20) return null;
  const header = await readExact(handle, 0, 20, fileSize, "coff");
  const architecture = peArchitecture(header.readUInt16LE(0));
  const sectionCount = header.readUInt16LE(2);
  const optionalHeaderBytes = header.readUInt16LE(16);
  if (architecture === null || sectionCount === 0 || sectionCount > 96 || optionalHeaderBytes !== 0
    || 20 + sectionCount * 40 > fileSize) return null;
  return inspection("coff", "toolchain", [architecture], "a COFF object file");
}

async function inspectElf(handle: FileHandle, fileSize: number): Promise<NativeBinaryInspection> {
  const header = await readExact(handle, 0, 64, fileSize, "elf");
  if (header[4] !== 2 || header[5] !== 1 || header[6] !== 1) {
    throw new InvalidBinaryError("elf", "ELF content is not a supported little-endian 64-bit image");
  }
  const imageType = header.readUInt16LE(16);
  const architecture = elfArchitecture(header.readUInt16LE(18));
  const version = header.readUInt32LE(20);
  const entryPoint = safeNumber(header.readBigUInt64LE(24), "elf", "entry point");
  const programHeaderOffset = safeNumber(header.readBigUInt64LE(32), "elf", "program-header offset");
  const elfHeaderBytes = header.readUInt16LE(52);
  const programHeaderBytes = header.readUInt16LE(54);
  const programHeaderCount = header.readUInt16LE(56);
  if (architecture === null || version !== 1 || elfHeaderBytes !== 64) {
    throw new InvalidBinaryError("elf", "ELF content has invalid or unsupported image metadata");
  }
  if (imageType === 1) return inspection("elf", "toolchain", [architecture], "an ELF relocatable object");
  if (imageType !== 2 && imageType !== 3) {
    throw new InvalidBinaryError("elf", "ELF content has an unsupported image type");
  }
  if (programHeaderOffset < 64 || programHeaderBytes < 56 || programHeaderCount === 0 || programHeaderCount > 128
    || programHeaderOffset + programHeaderBytes * programHeaderCount > fileSize) {
    throw new InvalidBinaryError("elf", "ELF content has an invalid or truncated program-header table");
  }
  let hasInterpreter = false;
  for (let index = 0; index < programHeaderCount; index++) {
    const programHeader = await readExact(
      handle,
      programHeaderOffset + index * programHeaderBytes,
      56,
      fileSize,
      "elf",
    );
    const segmentType = programHeader.readUInt32LE(0);
    const segmentOffset = safeNumber(programHeader.readBigUInt64LE(8), "elf", "segment offset");
    const segmentBytes = safeNumber(programHeader.readBigUInt64LE(32), "elf", "segment size");
    if (segmentOffset + segmentBytes > fileSize) {
      throw new InvalidBinaryError("elf", "ELF content has a segment outside the file");
    }
    if (segmentType === 3) hasInterpreter = true;
  }
  return imageType === 2 || hasInterpreter || entryPoint !== 0
    ? inspection("elf", "executable", [architecture], "an ELF executable image")
    : inspection("elf", "shared-library", [architecture], "an ELF shared library");
}

async function inspectMach(handle: FileHandle, fileSize: number, magicBytes: Buffer): Promise<NativeBinaryInspection> {
  const magic = machMagic(magicBytes);
  if (magic === null) throw new InvalidBinaryError("mach-o", "Mach-O content has an invalid magic value");
  if (magic.kind === "thin") return await inspectThinMach(handle, 0, fileSize, magic);

  const header = await readExact(handle, 0, 8, fileSize, "mach-o");
  const architectureCount = readUInt32(header, 4, magic.littleEndian);
  const architectureRecordBytes = magic.is64Bit ? 32 : 20;
  if (architectureCount === 0 || architectureCount > 16
    || 8 + architectureCount * architectureRecordBytes > fileSize) {
    throw new InvalidBinaryError("mach-o", "fat Mach-O content has an invalid architecture table");
  }
  const slices: Array<{ readonly offset: number; readonly size: number }> = [];
  const inspections: NativeBinaryInspection[] = [];
  for (let index = 0; index < architectureCount; index++) {
    const record = await readExact(
      handle,
      8 + index * architectureRecordBytes,
      architectureRecordBytes,
      fileSize,
      "mach-o",
    );
    const declaredCpu = readUInt32(record, 0, magic.littleEndian);
    const offset = magic.is64Bit
      ? safeNumber(readBigUInt64(record, 8, magic.littleEndian), "mach-o", "fat slice offset")
      : readUInt32(record, 8, magic.littleEndian);
    const size = magic.is64Bit
      ? safeNumber(readBigUInt64(record, 16, magic.littleEndian), "mach-o", "fat slice size")
      : readUInt32(record, 12, magic.littleEndian);
    if (size === 0 || offset < 8 + architectureCount * architectureRecordBytes || offset + size > fileSize
      || slices.some((slice) => offset < slice.offset + slice.size && slice.offset < offset + size)) {
      throw new InvalidBinaryError("mach-o", "fat Mach-O content has an invalid or overlapping architecture slice");
    }
    slices.push({ offset, size });
    const sliceMagicBytes = await readExact(handle, offset, 4, fileSize, "mach-o");
    const sliceMagic = machMagic(sliceMagicBytes);
    if (sliceMagic === null || sliceMagic.kind !== "thin") {
      throw new InvalidBinaryError("mach-o", "fat Mach-O content contains a non-Mach-O or nested-fat slice");
    }
    const slice = await inspectThinMach(handle, offset, size, sliceMagic);
    const actualCpu = machCpuFromArchitecture(slice.architectures[0]!);
    if (actualCpu !== null && actualCpu !== declaredCpu) {
      throw new InvalidBinaryError("mach-o", "fat Mach-O architecture metadata disagrees with its slice");
    }
    inspections.push(slice);
  }
  const architectures = [...new Set(inspections.flatMap((item) => item.architectures))].sort();
  if (inspections.some((item) => item.kind === "executable")) {
    return inspection("mach-o", "executable", architectures, "a fat Mach-O containing an executable image");
  }
  if (inspections.some((item) => item.kind === "toolchain")) {
    return inspection("mach-o", "toolchain", architectures, "a fat Mach-O containing toolchain content");
  }
  if (inspections.some((item) => item.kind !== "shared-library")) {
    throw new InvalidBinaryError("mach-o", "fat Mach-O content contains an invalid image slice");
  }
  return inspection("mach-o", "shared-library", architectures, "a fat Mach-O shared library");
}

async function inspectThinMach(
  handle: FileHandle,
  sliceOffset: number,
  sliceSize: number,
  magic: MachMagic,
): Promise<NativeBinaryInspection> {
  if (!magic.littleEndian) {
    throw new InvalidBinaryError("mach-o", "Mach-O content is not a supported little-endian image");
  }
  const headerBytes = magic.is64Bit ? 32 : 28;
  const header = await readExact(handle, sliceOffset, headerBytes, sliceOffset + sliceSize, "mach-o");
  const cpu = readUInt32(header, 4, true);
  const architecture = machArchitecture(cpu);
  const imageType = readUInt32(header, 12, true);
  const commandCount = readUInt32(header, 16, true);
  const commandBytes = readUInt32(header, 20, true);
  if (commandCount > 4096 || commandBytes > MAXIMUM_BINARY_HEADER_BYTES
    || headerBytes + commandBytes > sliceSize) {
    throw new InvalidBinaryError("mach-o", "Mach-O content has an invalid or truncated load-command table");
  }
  let commandOffset = sliceOffset + headerBytes;
  const commandEnd = commandOffset + commandBytes;
  let hasExecutableEntry = false;
  for (let index = 0; index < commandCount; index++) {
    const commandHeader = await readExact(handle, commandOffset, 8, sliceOffset + sliceSize, "mach-o");
    const command = commandHeader.readUInt32LE(0);
    const size = commandHeader.readUInt32LE(4);
    if (size < 8 || commandOffset + size > commandEnd || size % (magic.is64Bit ? 8 : 4) !== 0) {
      throw new InvalidBinaryError("mach-o", "Mach-O content has an invalid load command");
    }
    if (command === 0x5 || command === 0x80000028) hasExecutableEntry = true;
    commandOffset += size;
  }
  if (commandOffset !== commandEnd) {
    throw new InvalidBinaryError("mach-o", "Mach-O load commands do not cover the declared command bytes");
  }
  if ([1, 9, 10].includes(imageType)) {
    return inspection("mach-o", "toolchain", [architecture], "Mach-O object or debug toolchain content");
  }
  if ([2, 3, 4, 5, 7].includes(imageType) || hasExecutableEntry) {
    return inspection("mach-o", "executable", [architecture], "a Mach-O executable image");
  }
  if (imageType === 6 || imageType === 8) {
    return inspection("mach-o", "shared-library", [architecture], "a Mach-O shared library or bundle");
  }
  throw new InvalidBinaryError("mach-o", "Mach-O content has an unsupported image type");
}

function peArchitecture(machine: number): string | null {
  if (machine === 0x8664) return "x64";
  if (machine === 0xaa64) return "arm64";
  return null;
}

function elfArchitecture(machine: number): string | null {
  if (machine === 62) return "x64";
  if (machine === 183) return "arm64";
  return null;
}

function machArchitecture(cpu: number): string {
  if (cpu === 0x01000007) return "x64";
  if (cpu === 0x0100000c) return "arm64";
  return `mach-cpu-${cpu}`;
}

function machCpuFromArchitecture(architecture: string): number | null {
  if (architecture === "x64") return 0x01000007;
  if (architecture === "arm64") return 0x0100000c;
  const value = /^mach-cpu-(\d+)$/u.exec(architecture)?.[1];
  return value === undefined ? null : Number(value);
}

interface MachMagic {
  readonly kind: "thin" | "fat";
  readonly littleEndian: boolean;
  readonly is64Bit: boolean;
}

function machMagic(value: Buffer): MachMagic | null {
  switch (value.toString("hex")) {
    case "cefaedfe": return { kind: "thin", littleEndian: true, is64Bit: false };
    case "cffaedfe": return { kind: "thin", littleEndian: true, is64Bit: true };
    case "feedface": return { kind: "thin", littleEndian: false, is64Bit: false };
    case "feedfacf": return { kind: "thin", littleEndian: false, is64Bit: true };
    case "cafebabe": return { kind: "fat", littleEndian: false, is64Bit: false };
    case "bebafeca": return { kind: "fat", littleEndian: true, is64Bit: false };
    case "cafebabf": return { kind: "fat", littleEndian: false, is64Bit: true };
    case "bfbafeca": return { kind: "fat", littleEndian: true, is64Bit: true };
    default: return null;
  }
}

function readUInt32(buffer: Buffer, offset: number, littleEndian: boolean): number {
  return littleEndian ? buffer.readUInt32LE(offset) : buffer.readUInt32BE(offset);
}

function readBigUInt64(buffer: Buffer, offset: number, littleEndian: boolean): bigint {
  return littleEndian ? buffer.readBigUInt64LE(offset) : buffer.readBigUInt64BE(offset);
}

async function readAvailable(handle: FileHandle, position: number, bytes: number): Promise<Buffer> {
  if (bytes === 0) return Buffer.alloc(0);
  const buffer = Buffer.alloc(bytes);
  const result = await handle.read(buffer, 0, bytes, position);
  return buffer.subarray(0, result.bytesRead);
}

async function readExact(
  handle: FileHandle,
  position: number,
  bytes: number,
  fileSize: number,
  format: NativeBinaryInspection["format"],
): Promise<Buffer> {
  if (!Number.isSafeInteger(position) || !Number.isSafeInteger(bytes) || position < 0 || bytes < 0
    || position > fileSize - bytes) {
    throw new InvalidBinaryError(format, `${format.toUpperCase()} content is truncated or has an out-of-range header`);
  }
  const buffer = Buffer.alloc(bytes);
  let read = 0;
  while (read < bytes) {
    const result = await handle.read(buffer, read, bytes - read, position + read);
    if (result.bytesRead === 0) {
      throw new InvalidBinaryError(format, `${format.toUpperCase()} content ended during metadata inspection`);
    }
    read += result.bytesRead;
  }
  return buffer;
}

function safeNumber(value: bigint, format: NativeBinaryInspection["format"], label: string): number {
  if (value > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new InvalidBinaryError(format, `${format.toUpperCase()} ${label} exceeds the supported file range`);
  }
  return Number(value);
}

function inspection(
  format: NativeBinaryInspection["format"],
  kind: NativeBinaryKind,
  architectures: readonly string[],
  detail: string,
): NativeBinaryInspection {
  return { format, kind, architectures, detail };
}

function opaqueInspection(): NativeBinaryInspection {
  return inspection("opaque", "opaque", [], "opaque package data");
}

class InvalidBinaryError extends Error {
  public constructor(
    public readonly format: NativeBinaryInspection["format"],
    message: string,
  ) {
    super(message);
  }
}
