import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { ZigExtractor } from '../../extractors/zig-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('ZigExtractor', () => {
  let parserManager: ParserManager;

    beforeAll(async () => {
    // Initialize logger for tests
    const { initializeLogger } = await import('../../utils/logger.js');
    const { MillerPaths } = await import('../../utils/miller-paths.js');
    const paths = new MillerPaths(process.cwd());
    await paths.ensureDirectories();
    initializeLogger(paths);

    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Structs and Data Types', () => {
    it('should extract structs, unions, and enums', async () => {
      const zigCode = `
const std = @import("std");
const testing = std.testing;

// Basic struct
const Point = struct {
    x: f32,
    y: f32,

    const Self = @This();

    pub fn init(x: f32, y: f32) Self {
        return Self{ .x = x, .y = y };
    }

    pub fn distance(self: Self, other: Self) f32 {
        const dx = self.x - other.x;
        const dy = self.y - other.y;
        return @sqrt(dx * dx + dy * dy);
    }

    pub fn scale(self: *Self, factor: f32) void {
        self.x *= factor;
        self.y *= factor;
    }

    const ORIGIN = Point{ .x = 0.0, .y = 0.0 };
};

// Packed struct for memory layout control
const PackedData = packed struct {
    flags: u8,
    id: u16,
    value: u32,

    pub fn isValid(self: PackedData) bool {
        return self.flags & 0x80 != 0;
    }
};

// Generic struct
fn Vector(comptime T: type) type {
    return struct {
        items: []T,
        capacity: usize,
        allocator: std.mem.Allocator,

        const Self = @This();

        pub fn init(allocator: std.mem.Allocator) Self {
            return Self{
                .items = &[_]T{},
                .capacity = 0,
                .allocator = allocator,
            };
        }

        pub fn deinit(self: *Self) void {
            if (self.capacity > 0) {
                self.allocator.free(self.items.ptr[0..self.capacity]);
            }
        }

        pub fn append(self: *Self, item: T) !void {
            if (self.items.len == self.capacity) {
                try self.grow();
            }
            self.items.len += 1;
            self.items[self.items.len - 1] = item;
        }

        fn grow(self: *Self) !void {
            const new_capacity = if (self.capacity == 0) 8 else self.capacity * 2;
            const new_memory = try self.allocator.alloc(T, new_capacity);

            if (self.capacity > 0) {
                std.mem.copy(T, new_memory, self.items);
                self.allocator.free(self.items.ptr[0..self.capacity]);
            }

            self.items.ptr = new_memory.ptr;
            self.capacity = new_capacity;
        }
    };
}

// Union types
const Value = union(enum) {
    none: void,
    integer: i64,
    float: f64,
    string: []const u8,
    boolean: bool,

    pub fn typeString(self: Value) []const u8 {
        return switch (self) {
            .none => "none",
            .integer => "integer",
            .float => "float",
            .string => "string",
            .boolean => "boolean",
        };
    }

    pub fn asInteger(self: Value) ?i64 {
        return switch (self) {
            .integer => |val| val,
            else => null,
        };
    }
};

// Enum with explicit values
const Color = enum(u8) {
    red = 0xFF0000,
    green = 0x00FF00,
    blue = 0x0000FF,

    pub fn toRgb(self: Color) u32 {
        return @enumToInt(self);
    }
};

// Error set
const FileError = error{
    AccessDenied,
    OutOfMemory,
    FileNotFound,
    InvalidPath,
};

const AllocationError = error{
    OutOfMemory,
};

const IoError = error{
    NetworkDown,
    ConnectionRefused,
} || FileError;`;

      const result = await parserManager.parseFile('test.zig', zigCode);

      const extractor = new ZigExtractor('zig', 'test.zig', zigCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract structs
      const pointStruct = symbols.find(s => s.name === 'Point' && s.kind === SymbolKind.Class);
      expect(pointStruct).toBeDefined();
      expect(pointStruct?.signature).toContain('const Point = struct');

      const packedStruct = symbols.find(s => s.name === 'PackedData');
      expect(packedStruct).toBeDefined();
      expect(packedStruct?.signature).toContain('packed struct');

      // Should extract struct fields
      const xField = symbols.find(s => s.name === 'x' && s.kind === SymbolKind.Field);
      expect(xField).toBeDefined();
      expect(xField?.signature).toContain('f32');

      const flagsField = symbols.find(s => s.name === 'flags');
      expect(flagsField).toBeDefined();
      expect(flagsField?.signature).toContain('u8');

      // Should extract struct methods
      const initMethod = symbols.find(s => s.name === 'init' && s.kind === SymbolKind.Method);
      expect(initMethod).toBeDefined();
      expect(initMethod?.signature).toContain('pub fn init');

      const distanceMethod = symbols.find(s => s.name === 'distance');
      expect(distanceMethod).toBeDefined();
      expect(distanceMethod?.signature).toContain('f32');

      const scaleMethod = symbols.find(s => s.name === 'scale');
      expect(scaleMethod).toBeDefined();
      expect(scaleMethod?.signature).toContain('*Self');

      // Should extract constants
      const originConstant = symbols.find(s => s.name === 'ORIGIN');
      expect(originConstant).toBeDefined();
      expect(originConstant?.kind).toBe(SymbolKind.Constant);

      // Should extract generic functions
      const vectorFunction = symbols.find(s => s.name === 'Vector' && s.kind === SymbolKind.Function);
      expect(vectorFunction).toBeDefined();
      expect(vectorFunction?.signature).toContain('comptime T: type');

      // Should extract unions
      const valueUnion = symbols.find(s => s.name === 'Value');
      expect(valueUnion).toBeDefined();
      expect(valueUnion?.signature).toContain('union(enum)');

      // Should extract union methods
      const typeStringMethod = symbols.find(s => s.name === 'typeString');
      expect(typeStringMethod).toBeDefined();

      const asIntegerMethod = symbols.find(s => s.name === 'asInteger');
      expect(asIntegerMethod).toBeDefined();
      expect(asIntegerMethod?.signature).toContain('?i64');

      // Should extract enums
      const colorEnum = symbols.find(s => s.name === 'Color' && s.kind === SymbolKind.Enum);
      expect(colorEnum).toBeDefined();
      expect(colorEnum?.signature).toContain('enum(u8)');

      // Should extract enum members
      const redMember = symbols.find(s => s.name === 'red');
      expect(redMember).toBeDefined();

      const toRgbMethod = symbols.find(s => s.name === 'toRgb');
      expect(toRgbMethod).toBeDefined();

      // Should extract error sets
      const fileError = symbols.find(s => s.name === 'FileError');
      expect(fileError).toBeDefined();
      expect(fileError?.signature).toContain('error{');

      const ioError = symbols.find(s => s.name === 'IoError');
      expect(ioError).toBeDefined();
      expect(ioError?.signature).toContain('|| FileError');
    });
  });

  describe('Functions and Error Handling', () => {
    it('should extract functions with error handling and optionals', async () => {
      const zigCode = `
const std = @import("std");
const Allocator = std.mem.Allocator;

// Function with error union return type
fn parseInteger(input: []const u8) !i32 {
    if (input.len == 0) return error.EmptyInput;

    var result: i32 = 0;
    var negative = false;
    var start_idx: usize = 0;

    if (input[0] == '-') {
        negative = true;
        start_idx = 1;
        if (input.len == 1) return error.InvalidFormat;
    }

    for (input[start_idx..]) |char| {
        if (char < '0' or char > '9') return error.InvalidCharacter;

        const digit = char - '0';
        const new_result = std.math.mul(i32, result, 10) catch return error.Overflow;
        result = std.math.add(i32, new_result, digit) catch return error.Overflow;
    }

    return if (negative) -result else result;
}

// Function with optional return type
fn findChar(haystack: []const u8, needle: u8) ?usize {
    for (haystack, 0..) |char, index| {
        if (char == needle) return index;
    }
    return null;
}

// Generic function with multiple type parameters
fn swap(comptime T: type, a: *T, b: *T) void {
    const temp = a.*;
    a.* = b.*;
    b.* = temp;
}

// Function with allocator parameter
fn duplicateString(allocator: Allocator, input: []const u8) ![]u8 {
    const result = try allocator.alloc(u8, input.len);
    std.mem.copy(u8, result, input);
    return result;
}

// Async function
fn fetchData(url: []const u8) ![]u8 {
    var client = std.http.Client{ .allocator = std.heap.page_allocator };
    defer client.deinit();

    const response = try client.fetch(.{
        .location = .{ .url = url },
        .method = .GET,
    });

    return response.body orelse error.EmptyResponse;
}

// Function with comptime parameters
fn createArray(comptime T: type, comptime size: usize, value: T) [size]T {
    var array: [size]T = undefined;
    for (&array) |*item| {
        item.* = value;
    }
    return array;
}

// Inline function
inline fn min(a: anytype, b: @TypeOf(a)) @TypeOf(a) {
    return if (a < b) a else b;
}

// Export function (C ABI)
export fn add_numbers(a: c_int, b: c_int) c_int {
    return a + b;
}

// Function with varargs
fn printf(comptime fmt: []const u8, args: anytype) void {
    std.debug.print(fmt, args);
}

// Function pointer type
const BinaryOp = fn (a: i32, b: i32) i32;

fn applyOperation(a: i32, b: i32, op: BinaryOp) i32 {
    return op(a, b);
}

// Closure-like behavior with struct
const Counter = struct {
    value: i32 = 0,

    pub fn increment(self: *Counter) i32 {
        self.value += 1;
        return self.value;
    }

    pub fn reset(self: *Counter) void {
        self.value = 0;
    }
};`;

      const result = await parserManager.parseFile('test.zig', zigCode);

      const extractor = new ZigExtractor('zig', 'test.zig', zigCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract functions with error union returns
      const parseIntegerFn = symbols.find(s => s.name === 'parseInteger' && s.kind === SymbolKind.Function);
      expect(parseIntegerFn).toBeDefined();
      expect(parseIntegerFn?.signature).toContain('!i32');

      // Should extract functions with optional returns
      const findCharFn = symbols.find(s => s.name === 'findChar');
      expect(findCharFn).toBeDefined();
      expect(findCharFn?.signature).toContain('?usize');

      // Should extract generic functions
      const swapFn = symbols.find(s => s.name === 'swap');
      expect(swapFn).toBeDefined();
      expect(swapFn?.signature).toContain('comptime T: type');

      // Should extract functions with allocator parameters
      const duplicateStringFn = symbols.find(s => s.name === 'duplicateString');
      expect(duplicateStringFn).toBeDefined();
      expect(duplicateStringFn?.signature).toContain('Allocator');

      // Should extract async functions
      const fetchDataFn = symbols.find(s => s.name === 'fetchData');
      expect(fetchDataFn).toBeDefined();

      // Should extract comptime functions
      const createArrayFn = symbols.find(s => s.name === 'createArray');
      expect(createArrayFn).toBeDefined();
      expect(createArrayFn?.signature).toContain('comptime size: usize');

      // Should extract inline functions
      const minFn = symbols.find(s => s.name === 'min');
      expect(minFn).toBeDefined();
      expect(minFn?.signature).toContain('inline fn');

      // Should extract export functions
      const addNumbersFn = symbols.find(s => s.name === 'add_numbers');
      expect(addNumbersFn).toBeDefined();
      expect(addNumbersFn?.signature).toContain('export fn');
      expect(addNumbersFn?.signature).toContain('c_int');

      // Should extract varargs functions
      const printfFn = symbols.find(s => s.name === 'printf');
      expect(printfFn).toBeDefined();
      expect(printfFn?.signature).toContain('anytype');

      // Should extract function types
      const binaryOpType = symbols.find(s => s.name === 'BinaryOp');
      expect(binaryOpType).toBeDefined();
      expect(binaryOpType?.signature).toContain('fn (');

      const applyOpFn = symbols.find(s => s.name === 'applyOperation');
      expect(applyOpFn).toBeDefined();
      expect(applyOpFn?.signature).toContain('BinaryOp');

      // Should extract counter struct and methods
      const counterStruct = symbols.find(s => s.name === 'Counter');
      expect(counterStruct).toBeDefined();

      const incrementMethod = symbols.find(s => s.name === 'increment');
      expect(incrementMethod).toBeDefined();

      const resetMethod = symbols.find(s => s.name === 'reset');
      expect(resetMethod).toBeDefined();
    });
  });

  describe('Memory Management and C Interop', () => {
    it('should extract memory management patterns and C integration', async () => {
      const zigCode = `
const std = @import("std");
const c = @cImport({
    @cInclude("stdio.h");
    @cInclude("stdlib.h");
    @cInclude("string.h");
});

// Allocator wrapper
const ArenaAllocator = struct {
    arena: std.heap.ArenaAllocator,

    pub fn init(backing_allocator: std.mem.Allocator) ArenaAllocator {
        return ArenaAllocator{
            .arena = std.heap.ArenaAllocator.init(backing_allocator),
        };
    }

    pub fn allocator(self: *ArenaAllocator) std.mem.Allocator {
        return self.arena.allocator();
    }

    pub fn deinit(self: *ArenaAllocator) void {
        self.arena.deinit();
    }

    pub fn reset(self: *ArenaAllocator, mode: std.heap.ArenaAllocator.ResetMode) void {
        _ = self.arena.reset(mode);
    }
};

// C interop structure
const CString = extern struct {
    data: [*:0]u8,
    length: c_size_t,

    pub fn fromSlice(allocator: std.mem.Allocator, slice: []const u8) !CString {
        const data = try allocator.allocSentinel(u8, slice.len, 0);
        std.mem.copy(u8, data, slice);
        return CString{
            .data = data,
            .length = slice.len,
        };
    }

    pub fn toSlice(self: CString) []const u8 {
        return self.data[0..self.length];
    }

    pub fn deinit(self: CString, allocator: std.mem.Allocator) void {
        allocator.free(self.data[0..self.length + 1]);
    }
};

// C function declarations
extern "c" fn malloc(size: c_size_t) ?*anyopaque;
extern "c" fn free(ptr: *anyopaque) void;
extern "c" fn printf(format: [*:0]const u8, ...) c_int;

// C callback function type
const CallbackFn = fn (data: ?*anyopaque, result: c_int) callconv(.C) void;

// Library with C bindings
const MathLib = struct {
    // Function that calls C math functions
    pub fn fastSqrt(value: f64) f64 {
        return @sqrt(value);
    }

    // Wrapper for C malloc/free
    pub fn cAlloc(size: usize) ?[]u8 {
        const ptr = malloc(size) orelse return null;
        return @ptrCast([*]u8, ptr)[0..size];
    }

    pub fn cFree(memory: []u8) void {
        free(memory.ptr);
    }
};

// Smart pointer pattern
fn UniquePtr(comptime T: type) type {
    return struct {
        ptr: ?*T,
        allocator: std.mem.Allocator,

        const Self = @This();

        pub fn init(allocator: std.mem.Allocator, value: T) !Self {
            const ptr = try allocator.create(T);
            ptr.* = value;
            return Self{
                .ptr = ptr,
                .allocator = allocator,
            };
        }

        pub fn deinit(self: *Self) void {
            if (self.ptr) |ptr| {
                self.allocator.destroy(ptr);
                self.ptr = null;
            }
        }

        pub fn get(self: Self) ?*T {
            return self.ptr;
        }

        pub fn release(self: *Self) ?*T {
            const ptr = self.ptr;
            self.ptr = null;
            return ptr;
        }

        pub fn reset(self: *Self, new_value: ?T) !void {
            self.deinit();
            if (new_value) |value| {
                const ptr = try self.allocator.create(T);
                ptr.* = value;
                self.ptr = ptr;
            }
        }
    };
}

// RAII pattern
const FileHandle = struct {
    file: ?*c.FILE = null,

    const Self = @This();

    pub fn open(path: [*:0]const u8, mode: [*:0]const u8) !Self {
        const file = c.fopen(path, mode) orelse return error.CannotOpenFile;
        return Self{ .file = file };
    }

    pub fn close(self: *Self) void {
        if (self.file) |file| {
            _ = c.fclose(file);
            self.file = null;
        }
    }

    pub fn write(self: Self, data: []const u8) !usize {
        const file = self.file orelse return error.FileNotOpen;
        const written = c.fwrite(data.ptr, 1, data.len, file);
        if (written != data.len) return error.WriteError;
        return written;
    }

    pub fn read(self: Self, buffer: []u8) !usize {
        const file = self.file orelse return error.FileNotOpen;
        return c.fread(buffer.ptr, 1, buffer.len, file);
    }
};`;

      const result = await parserManager.parseFile('test.zig', zigCode);

      const extractor = new ZigExtractor('zig', 'test.zig', zigCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract allocator wrapper
      const arenaAllocator = symbols.find(s => s.name === 'ArenaAllocator');
      expect(arenaAllocator).toBeDefined();

      const allocatorMethod = symbols.find(s => s.name === 'allocator');
      expect(allocatorMethod).toBeDefined();

      // Should extract C interop structures
      const cStringStruct = symbols.find(s => s.name === 'CString');
      expect(cStringStruct).toBeDefined();
      expect(cStringStruct?.signature).toContain('extern struct');

      const fromSliceMethod = symbols.find(s => s.name === 'fromSlice');
      expect(fromSliceMethod).toBeDefined();

      // Should extract extern C functions
      const mallocFn = symbols.find(s => s.name === 'malloc');
      expect(mallocFn).toBeDefined();
      expect(mallocFn?.signature).toContain('extern "c"');

      const freeFn = symbols.find(s => s.name === 'free');
      expect(freeFn).toBeDefined();

      const printfFn = symbols.find(s => s.name === 'printf');
      expect(printfFn).toBeDefined();
      expect(printfFn?.signature).toContain('...');

      // Should extract callback function types
      const callbackType = symbols.find(s => s.name === 'CallbackFn');
      expect(callbackType).toBeDefined();
      expect(callbackType?.signature).toContain('callconv(.C)');

      // Should extract math library
      const mathLib = symbols.find(s => s.name === 'MathLib');
      expect(mathLib).toBeDefined();

      const fastSqrtFn = symbols.find(s => s.name === 'fastSqrt');
      expect(fastSqrtFn).toBeDefined();

      const cAllocFn = symbols.find(s => s.name === 'cAlloc');
      expect(cAllocFn).toBeDefined();

      // Should extract smart pointer pattern
      const uniquePtrFn = symbols.find(s => s.name === 'UniquePtr');
      expect(uniquePtrFn).toBeDefined();

      // Should extract RAII pattern
      const fileHandle = symbols.find(s => s.name === 'FileHandle');
      expect(fileHandle).toBeDefined();

      const openMethod = symbols.find(s => s.name === 'open');
      expect(openMethod).toBeDefined();

      const closeMethod = symbols.find(s => s.name === 'close');
      expect(closeMethod).toBeDefined();

      const writeMethod = symbols.find(s => s.name === 'write');
      expect(writeMethod).toBeDefined();

      const readMethod = symbols.find(s => s.name === 'read');
      expect(readMethod).toBeDefined();
    });
  });

  describe('Testing and Build Integration', () => {
    it('should extract test functions and build configurations', async () => {
      const zigCode = `
const std = @import("std");
const testing = std.testing;
const expect = testing.expect;
const expectEqual = testing.expectEqual;

// Test functions
test "basic arithmetic" {
    try expect(2 + 2 == 4);
    try expect(10 - 5 == 5);
    try expect(3 * 4 == 12);
    try expect(8 / 2 == 4);
}

test "string operations" {
    const str = "Hello, World!";
    try expect(str.len == 13);
    try expect(std.mem.eql(u8, str[0..5], "Hello"));
}

test "memory allocation" {
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();

    const allocator = arena.allocator();
    const memory = try allocator.alloc(u8, 100);
    try expect(memory.len == 100);
}

test "error handling" {
    const result = parseNumber("123");
    try expectEqual(@as(i32, 123), result);

    const error_result = parseNumber("abc");
    try testing.expectError(error.InvalidCharacter, error_result);
}

// Benchmark test
test "performance benchmark" {
    const iterations = 1000000;
    var sum: u64 = 0;

    const start = std.time.nanoTimestamp();
    for (0..iterations) |i| {
        sum += i;
    }
    const end = std.time.nanoTimestamp();

    const duration = end - start;
    std.debug.print("Sum calculation took {} ns\\n", .{duration});

    try expect(sum == (iterations * (iterations - 1)) / 2);
}

// Helper function for tests
fn parseNumber(input: []const u8) !i32 {
    return std.fmt.parseInt(i32, input, 10);
}

// Build configuration
pub const BuildConfig = struct {
    target: std.Target = .{},
    optimize: std.builtin.OptimizeMode = .Debug,
    linkage: std.builtin.LinkMode = .Dynamic,

    pub fn create(b: *std.Build) BuildConfig {
        return BuildConfig{
            .target = b.standardTargetOptions(.{}),
            .optimize = b.standardOptimizeOption(.{}),
        };
    }
};

// Compile-time constants and functions
const VERSION_MAJOR = 1;
const VERSION_MINOR = 0;
const VERSION_PATCH = 0;

comptime {
    if (VERSION_MAJOR < 1) {
        @compileError("Version major must be at least 1");
    }
}

fn versionString() []const u8 {
    return std.fmt.comptimePrint("{}.{}.{}", .{ VERSION_MAJOR, VERSION_MINOR, VERSION_PATCH });
}

// Conditional compilation
const features = struct {
    const debug_mode = @import("builtin").mode == .Debug;
    const target_os = @import("builtin").target.os.tag;
    const is_windows = target_os == .windows;
    const is_linux = target_os == .linux;
    const is_macos = target_os == .macos;
};

// Platform-specific code
const PlatformApi = switch (features.target_os) {
    .windows => struct {
        pub fn getCurrentDirectory() ![]u8 {
            // Windows implementation
            return error.NotImplemented;
        }
    },
    .linux, .macos => struct {
        pub fn getCurrentDirectory() ![]u8 {
            // Unix implementation
            return error.NotImplemented;
        }
    },
    else => struct {
        pub fn getCurrentDirectory() ![]u8 {
            return error.UnsupportedPlatform;
        }
    },
};`;

      const result = await parserManager.parseFile('test.zig', zigCode);

      const extractor = new ZigExtractor('zig', 'test.zig', zigCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract test functions
      const basicArithmeticTest = symbols.find(s => s.name === 'basic arithmetic');
      expect(basicArithmeticTest).toBeDefined();
      expect(basicArithmeticTest?.signature).toContain('test "basic arithmetic"');

      const stringOpsTest = symbols.find(s => s.name === 'string operations');
      expect(stringOpsTest).toBeDefined();

      const memoryTest = symbols.find(s => s.name === 'memory allocation');
      expect(memoryTest).toBeDefined();

      const errorTest = symbols.find(s => s.name === 'error handling');
      expect(errorTest).toBeDefined();

      const benchmarkTest = symbols.find(s => s.name === 'performance benchmark');
      expect(benchmarkTest).toBeDefined();

      // Should extract helper functions
      const parseNumberFn = symbols.find(s => s.name === 'parseNumber');
      expect(parseNumberFn).toBeDefined();

      // Should extract build configuration
      const buildConfig = symbols.find(s => s.name === 'BuildConfig');
      expect(buildConfig).toBeDefined();

      const createMethod = symbols.find(s => s.name === 'create');
      expect(createMethod).toBeDefined();

      // Should extract compile-time constants
      const versionMajor = symbols.find(s => s.name === 'VERSION_MAJOR');
      expect(versionMajor).toBeDefined();
      expect(versionMajor?.kind).toBe(SymbolKind.Constant);

      const versionMinor = symbols.find(s => s.name === 'VERSION_MINOR');
      expect(versionMinor).toBeDefined();

      const versionPatch = symbols.find(s => s.name === 'VERSION_PATCH');
      expect(versionPatch).toBeDefined();

      // Should extract comptime functions
      const versionStringFn = symbols.find(s => s.name === 'versionString');
      expect(versionStringFn).toBeDefined();

      // Should extract conditional compilation structures
      const featuresStruct = symbols.find(s => s.name === 'features');
      expect(featuresStruct).toBeDefined();

      const debugMode = symbols.find(s => s.name === 'debug_mode');
      expect(debugMode).toBeDefined();

      const targetOs = symbols.find(s => s.name === 'target_os');
      expect(targetOs).toBeDefined();

      // Should extract platform-specific API
      const platformApi = symbols.find(s => s.name === 'PlatformApi');
      expect(platformApi).toBeDefined();
      expect(platformApi?.signature).toContain('switch');

      const getCurrentDirFn = symbols.find(s => s.name === 'getCurrentDirectory');
      expect(getCurrentDirFn).toBeDefined();
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer types and extract relationships', async () => {
      const zigCode = `
const std = @import("std");

const BaseShape = struct {
    x: f32,
    y: f32,

    pub fn area(self: BaseShape) f32 {
        _ = self;
        return 0.0;
    }
};

const Rectangle = struct {
    base: BaseShape,
    width: f32,
    height: f32,

    pub fn init(x: f32, y: f32, width: f32, height: f32) Rectangle {
        return Rectangle{
            .base = BaseShape{ .x = x, .y = y },
            .width = width,
            .height = height,
        };
    }

    pub fn area(self: Rectangle) f32 {
        return self.width * self.height;
    }
};

const Circle = struct {
    base: BaseShape,
    radius: f32,

    pub fn init(x: f32, y: f32, radius: f32) Circle {
        return Circle{
            .base = BaseShape{ .x = x, .y = y },
            .radius = radius,
        };
    }

    pub fn area(self: Circle) f32 {
        return std.math.pi * self.radius * self.radius;
    }
};

// Type alias
const ShapeList = std.ArrayList(BaseShape);

// Function that works with multiple types
fn calculateTotalArea(shapes: []const BaseShape) f32 {
    var total: f32 = 0.0;
    for (shapes) |shape| {
        total += shape.area();
    }
    return total;
}

// Generic container
const Container(comptime T: type) = struct {
    data: []T,
    allocator: std.mem.Allocator,

    const Self = @This();

    pub fn init(allocator: std.mem.Allocator) Self {
        return Self{
            .data = &[_]T{},
            .allocator = allocator,
        };
    }

    pub fn add(self: *Self, item: T) !void {
        // Implementation
    }
};`;

      const result = await parserManager.parseFile('test.zig', zigCode);

      const extractor = new ZigExtractor('zig', 'test.zig', zigCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);
      const types = extractor.inferTypes(symbols);

      // Should extract composition relationships
      expect(relationships.length).toBeGreaterThan(0);

      const rectangleComposition = relationships.find(r =>
        r.kind === 'composition' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Rectangle'
      );
      expect(rectangleComposition).toBeDefined();

      const circleComposition = relationships.find(r =>
        r.kind === 'composition' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Circle'
      );
      expect(circleComposition).toBeDefined();

      // Should infer types
      expect(types.size).toBeGreaterThan(0);

      // Should identify numeric types
      const xField = symbols.find(s => s.name === 'x');
      if (xField) {
        const inferredType = types.get(xField.id);
        if (inferredType) {
          expect(inferredType).toContain('f32');
        }
      }

      const radiusField = symbols.find(s => s.name === 'radius');
      if (radiusField) {
        const inferredType = types.get(radiusField.id);
        if (inferredType) {
          expect(inferredType).toContain('f32');
        }
      }

      // Should extract type aliases
      const shapeListType = symbols.find(s => s.name === 'ShapeList');
      expect(shapeListType).toBeDefined();
      expect(shapeListType?.signature).toContain('std.ArrayList(BaseShape)');

      // Should extract generic types
      const containerType = symbols.find(s => s.name === 'Container');
      expect(containerType).toBeDefined();
      expect(containerType?.signature).toContain('comptime T: type');

      // Should handle polymorphic function calls
      const calculateAreaFn = symbols.find(s => s.name === 'calculateTotalArea');
      expect(calculateAreaFn).toBeDefined();
      expect(calculateAreaFn?.signature).toContain('[]const BaseShape');
    });
  });
});